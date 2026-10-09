using System.Collections.ObjectModel;
using AutoCardCopier.App.ViewModels.Pages;

namespace AutoCardCopier.App.ViewModels
{
    /// <summary>左侧导航项。</summary>
    public sealed class NavigationItem
    {
        public NavigationItem(string title, string glyph, object viewModel)
        {
            Title = title;
            Glyph = glyph;
            ViewModel = viewModel;
        }

        /// <summary>显示名称。</summary>
        public string Title { get; private set; }

        /// <summary>Segoe Fluent Icons 字形。</summary>
        public string Glyph { get; private set; }

        /// <summary>对应的页面视图模型。</summary>
        public object ViewModel { get; private set; }
    }

    /// <summary>主窗口视图模型：负责导航与页面切换。</summary>
    public sealed class MainViewModel : ViewModelBase
    {
        private NavigationItem _selectedNavigation;

        public MainViewModel(AppServices services)
        {
            NavigationItems = new ObservableCollection<NavigationItem>
            {
                new NavigationItem("卡片", "\uE722", new CardsViewModel(services)),
                new NavigationItem("项目库", "\uE8B7", new ProjectsViewModel(services)),
                new NavigationItem("历史", "\uE81C", new HistoryViewModel(services)),
                new NavigationItem("设置", "\uE713", new SettingsViewModel(services)),
                new NavigationItem("关于", "\uE946", new AboutViewModel(services))
            };
            SelectedNavigation = NavigationItems[0];
        }

        /// <summary>导航项集合。</summary>
        public ObservableCollection<NavigationItem> NavigationItems { get; private set; }

        /// <summary>当前选中的导航项；切换时同步更新内容区。</summary>
        public NavigationItem SelectedNavigation
        {
            get { return _selectedNavigation; }
            set
            {
                if (SetProperty(ref _selectedNavigation, value))
                {
                    RaisePropertyChanged("CurrentViewModel");
                }
            }
        }

        /// <summary>内容区当前显示的视图模型（由 App.xaml 中的 DataTemplate 决定用哪个页面渲染）。</summary>
        public object CurrentViewModel
        {
            get { return _selectedNavigation == null ? null : _selectedNavigation.ViewModel; }
        }

        /// <summary>窗口标题。</summary>
        public string WindowTitle { get { return "自动拷卡助手"; } }

        /// <summary>标题栏副标题（版本号）。</summary>
        public string VersionText { get { return "v" + AppServices.VersionText; } }
    }
}
