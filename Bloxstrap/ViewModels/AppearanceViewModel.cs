﻿using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

using Bloxstrap.Dialogs;
using Bloxstrap.Enums;
using Bloxstrap.Helpers.Extensions;
using Bloxstrap.Views;

namespace Bloxstrap.ViewModels
{
    public class AppearanceViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private readonly Page _page;

        public ICommand PreviewBootstrapperCommand => new RelayCommand(PreviewBootstrapper);

        private void PreviewBootstrapper()
        {
            IBootstrapperDialog dialog = App.Settings.Prop.BootstrapperStyle.GetNew();
            dialog.Message = "Style preview - Click Cancel to close";
            dialog.CancelEnabled = true;
            dialog.ShowBootstrapper();
        }

        public AppearanceViewModel(Page page)
        {
            _page = page;
        }

        public IReadOnlyDictionary<string, Theme> Themes { get; set; } = new Dictionary<string, Theme>()
        {
            { "System Default", Enums.Theme.Default },
            { "Light", Enums.Theme.Light },
            { "Dark", Enums.Theme.Dark },
        };

        public string Theme
        {
            get => Themes.FirstOrDefault(x => x.Value == App.Settings.Prop.Theme).Key;
            set
            {
                App.Settings.Prop.Theme = Themes[value];
                ((MainWindow)Window.GetWindow(_page)!).SetTheme();
            }
        }

        public IReadOnlyDictionary<string, BootstrapperStyle> Dialogs { get; set; } = new Dictionary<string, BootstrapperStyle>()
        {
            { "Fluent", BootstrapperStyle.FluentDialog },
            { "Progress (~2014)", BootstrapperStyle.ProgressDialog },
            { "Legacy (2011 - 2014)", BootstrapperStyle.LegacyDialog2011 },
            { "Legacy (2009 - 2011)", BootstrapperStyle.LegacyDialog2009 },
            { "Vista (2009 - 2011)", BootstrapperStyle.VistaDialog },
        };

        public string Dialog
        {
            get => Dialogs.FirstOrDefault(x => x.Value == App.Settings.Prop.BootstrapperStyle).Key;
            set => App.Settings.Prop.BootstrapperStyle = Dialogs[value];
        }

        public IReadOnlyDictionary<string, BootstrapperIcon> Icons { get; set; } = new Dictionary<string, BootstrapperIcon>()
        {
            { "Bloxstrap", BootstrapperIcon.IconBloxstrap },
            { "2022", BootstrapperIcon.Icon2022 },
            { "2019", BootstrapperIcon.Icon2019 },
            { "2017", BootstrapperIcon.Icon2017 },
            { "Late 2015", BootstrapperIcon.IconLate2015 },
            { "Early 2015", BootstrapperIcon.IconEarly2015 },
            { "2011", BootstrapperIcon.Icon2011 },
            { "2009", BootstrapperIcon.Icon2009 },
        };

        public string Icon
        {
            get => Icons.FirstOrDefault(x => x.Value == App.Settings.Prop.BootstrapperIcon).Key;
            set
            {
                App.Settings.Prop.BootstrapperIcon = Icons[value];
                OnPropertyChanged(nameof(IconPreviewSource));
            }
        }

        public ImageSource IconPreviewSource => App.Settings.Prop.BootstrapperIcon.GetIcon().GetImageSource();
    }
}
