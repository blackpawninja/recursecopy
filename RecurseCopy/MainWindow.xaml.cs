using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinForm = System.Windows.Forms;

namespace RecurseCopy
{
    class ProgressReport
    {
        public string Caption { get; set; }
        public string Description { get; set; }
        public int TotalTask { get; set; }
        public int DoneTask { get; set; }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int ReportProgressEveryMs = 100;

        private readonly WinForm.FolderBrowserDialog _folderBrowser = new WinForm.FolderBrowserDialog();
        private CancellationTokenSource _cts;

        public MainWindow()
        {
            InitializeComponent();

            FileFilter.Text = (string)Properties.Settings.Default["FileFilter"];
            SourceFolder.Text = (string)Properties.Settings.Default["SourceFolder"];
            TargetFolder.Text = (string)Properties.Settings.Default["TargetFolder"];
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel(); // cancel if task is running

            Properties.Settings.Default["FileFilter"] = FileFilter.Text;
            Properties.Settings.Default["SourceFolder"] = SourceFolder.Text;
            Properties.Settings.Default["TargetFolder"] = TargetFolder.Text;
            Properties.Settings.Default.Save();
        }
        
        private void BrowseFolder(TextBox t)
        {
            _folderBrowser.SelectedPath = t.Text;
            var result = _folderBrowser.ShowDialog();
            if (result == WinForm.DialogResult.Cancel) return;

            t.Text = _folderBrowser.SelectedPath;
        }

        private void BrowseSource_OnClick(object sender, RoutedEventArgs e)
        {
            BrowseFolder(SourceFolder);
        }

        private void BrowseTarget_OnClick(object sender, RoutedEventArgs e)
        {
            BrowseFolder(TargetFolder);
        }

        private List<string> CollectFiles(string filter, string source, CancellationToken token, IProgress<ProgressReport> progress)
        {
            var stopwatch = Stopwatch.StartNew();
            var status = new ProgressReport
            {
                Caption = "Collecting Files..."
            };

            var files = new List<string>();
            var filesEnum = Directory.EnumerateFiles(source, filter, SearchOption.AllDirectories);
            foreach (var filePath in filesEnum)
            {
                token.ThrowIfCancellationRequested();

                files.Add(filePath);

                if (stopwatch.ElapsedMilliseconds <= ReportProgressEveryMs) continue;
                status.Description = files.Count.ToString();
                progress.Report(status);
                stopwatch.Restart();
            }

            return files;
        }

        private void CopyFiles(
            List<string> files, string basePath, string destination, bool overwrite,
            CancellationToken token, IProgress<ProgressReport> progress
        )
        {
            var fileCount = files.Count;

            var stopwatch = Stopwatch.StartNew();
            var status = new ProgressReport
            {
                Caption = "Copying Files...",
                TotalTask = fileCount,
                DoneTask = 0
            };

            foreach (var filePath in files)
            {
                token.ThrowIfCancellationRequested();
                
                var sourcePath = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(sourcePath)) throw new FileFormatException($"Invalid path {sourcePath}");

                var dstPath = destination;
                if (sourcePath.Length > basePath.Length)
                {
                    var path = sourcePath.Substring(basePath.Length + 1); // naive basePath
                    dstPath = Path.Combine(destination, path);
                }

                var file = Path.GetFileName(filePath);
                var dstFile = Path.Combine(dstPath, file);

                // using check is more performant than catching IOException when file exists
                if (!File.Exists(dstFile))
                {
                    Directory.CreateDirectory(dstPath);
                    File.Copy(filePath, dstFile);
                }
                else if (overwrite)
                {
                    File.Copy(filePath, dstFile, true);
                }

                status.DoneTask++;
                if (stopwatch.ElapsedMilliseconds <= ReportProgressEveryMs) continue;
                status.Description = $"{status.DoneTask} of {fileCount}";
                progress.Report(status);
                stopwatch.Restart();
            }
        }

        private Task StartProcessing(
            string filter, string source, string target, bool overwrite,
            CancellationToken token, IProgress<ProgressReport> progress
        ) 
        {
            return Task.Run(() =>
            {
                var files = CollectFiles(filter, source, token, progress);
                CopyFiles(files, source, target, overwrite, token, progress);
            }, token);
        }

        private void Progress_OnUpdate(ProgressReport status)
        {
            ProgressCaption.Text = status.Caption;
            ProgressDescription.Text = status.Description;

            if (status.TotalTask > 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Minimum = 0;
                ProgressBar.Maximum = status.TotalTask;
                ProgressBar.Value = status.DoneTask;
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
            }
        }
        
        private async void StartButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SourceFolder.Text))
            {
                MessageBox.Show("Source Folder must be set");
                return;
            }

            if (string.IsNullOrEmpty(TargetFolder.Text))
            {
                MessageBox.Show("Target Folder must be set");
                return;
            }

            StartButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            ProgressStatus.Visibility = Visibility.Visible;

            _cts = new CancellationTokenSource();
            try
            {
                var filter = "*.*";
                if (!string.IsNullOrEmpty(FileFilter.Text)) filter = FileFilter.Text;

                var overwrite = false;
                if (OptionOverwrite.IsChecked.HasValue) overwrite = OptionOverwrite.IsChecked.Value;

                var progress = new Progress<ProgressReport>(Progress_OnUpdate);

                await StartProcessing(
                    filter, SourceFolder.Text, TargetFolder.Text, overwrite,
                    _cts.Token, progress
                );
            }
            catch (OperationCanceledException) {}
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            {
                // sometimes the copy ran too fast as if nothing happened
                // ensure progress bar is shown a short time so user know processing happened
                await Task.Delay(200);

                _cts = null;

                StopButton.Visibility = Visibility.Collapsed;
                StartButton.Visibility = Visibility.Visible;
                ProgressStatus.Visibility = Visibility.Collapsed;
            }
        }

        private void StopButton_OnClick(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }
    }
}
