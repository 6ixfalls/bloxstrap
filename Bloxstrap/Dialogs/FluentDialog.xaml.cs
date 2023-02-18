﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Bloxstrap.Enums;
using Bloxstrap.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

namespace Bloxstrap.Dialogs
{
    /// <summary>
    /// Interaction logic for FluentDialog.xaml
    /// </summary>
    public partial class FluentDialog : IBootstrapperDialog
    {
        private readonly IThemeService _themeService = new ThemeService();

        private readonly FluentDialogViewModel _viewModel;

        public Bootstrapper? Bootstrapper { get; set; }

        #region UI Elements
        public string Message
        {
            get => _viewModel.Message;
            set
            {
                _viewModel.Message = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.Message));
            }
        }

        public ProgressBarStyle ProgressStyle
        {
            get => _viewModel.ProgressIndeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            set
            {
                _viewModel.ProgressIndeterminate = (value == ProgressBarStyle.Marquee);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressIndeterminate));
            }
        }

        public int ProgressValue
        {
            get => _viewModel.ProgressValue;
            set
            {
                _viewModel.ProgressValue = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressValue));
            }
        }

        public bool CancelEnabled
        {
            get => _viewModel.CancelButtonVisibility == Visibility.Visible;
            set
            {
                _viewModel.CancelButtonVisibility = (value ? Visibility.Visible : Visibility.Collapsed);
                _viewModel.OnPropertyChanged(nameof(_viewModel.CancelButtonVisibility));
            }
        }
        #endregion

        public FluentDialog()
        {
            _viewModel = new FluentDialogViewModel(this);
            DataContext = _viewModel;
            Title = App.ProjectName;

            _themeService.SetTheme(App.Settings.Prop.Theme.GetFinal() == Enums.Theme.Dark ? ThemeType.Dark : ThemeType.Light);
            _themeService.SetSystemAccent();

            InitializeComponent();
        }

        #region IBootstrapperDialog Methods

        public void ShowBootstrapper() => this.ShowDialog();

        public void CloseBootstrapper() => Dispatcher.BeginInvoke(this.Close);

        // TODO: make prompts use dialog view natively rather than using message dialog boxes

        public void ShowSuccess(string message)
        {
            App.ShowMessageBox(message, MessageBoxImage.Information);
            App.Terminate();
        }

        public void ShowError(string message)
        {
            App.ShowMessageBox($"An error occurred while starting Roblox\n\nDetails: {message}", MessageBoxImage.Error);
            App.Terminate(Bootstrapper.ERROR_INSTALL_FAILURE);
        }

        public void PromptShutdown()
        {
            MessageBoxResult result = App.ShowMessageBox(
                "Roblox is currently running, but needs to close. Would you like close Roblox now?",
                MessageBoxImage.Information,
                MessageBoxButton.OKCancel
            );

            if (result != MessageBoxResult.OK)
                Environment.Exit(Bootstrapper.ERROR_INSTALL_USEREXIT);
        }
        #endregion
    }
}
