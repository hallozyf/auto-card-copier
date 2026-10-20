using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AutoCardCopier.Core.Copying;

namespace AutoCardCopier.App
{
    /// <summary>
    /// 同名冲突弹窗（F14-B）：覆盖 / 替换 / 都要，可勾选"本批剩余同样处理"。
    /// 用代码构建，避免为一个对话框增加 XAML 文件。
    /// </summary>
    public sealed class ConflictDialog : Window
    {
        private readonly CheckBox _applyAll;

        private ConflictDialog(ConflictContext context, string remainingText)
        {
            Title = "发现同名文件";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(18) };

            panel.Children.Add(Label("目标目录中已存在同名文件：", true));
            panel.Children.Add(Label(context.FileName, false));
            panel.Children.Add(Label(string.Format("已有 {0:N0} 字节　卡上 {1:N0} 字节　{2}",
                context.ExistingLength, context.SourceLength, remainingText), false));

            panel.Children.Add(Label("请选择处理方式：", true));

            var overwrite = Button("覆盖（删除旧文件，写入卡上的）", delegate { Finish(ConflictAction.Overwrite); });
            var replace = Button("替换（旧文件改名保留，再写入卡上的）", delegate { Finish(ConflictAction.Replace); });
            var keepBoth = Button("都要（两个都保留，新的加序号）", delegate { Finish(ConflictAction.KeepBoth); });
            panel.Children.Add(overwrite);
            panel.Children.Add(replace);
            panel.Children.Add(keepBoth);

            _applyAll = new CheckBox { Content = "本批剩余的同类冲突都按此处理", Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(_applyAll);

            Content = panel;
        }

        /// <summary>用户的选择。</summary>
        public ConflictAction Action { get; private set; }

        /// <summary>是否把本次选择应用到本批剩余冲突。</summary>
        public bool ApplyToRest { get { return _applyAll.IsChecked == true; } }

        private void Finish(ConflictAction action)
        {
            Action = action;
            DialogResult = true;
        }

        private static TextBlock Label(string text, bool bold)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
        }

        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 6, 10, 6) };
            button.Click += handler;
            return button;
        }

        /// <summary>询问一次冲突。</summary>
        public static ConflictAction Ask(Window owner, ConflictContext context, string remainingText,
            out bool applyToRest)
        {
            var dialog = new ConflictDialog(context, remainingText) { Owner = owner };
            bool? shown = dialog.ShowDialog();
            applyToRest = dialog.ApplyToRest;
            return shown == true ? dialog.Action : ConflictAction.Skip;
        }
    }

    /// <summary>把界面弹窗包装成冲突决策来源；勾选"本批剩余同样处理"后不再打扰。</summary>
    public sealed class UiConflictResolver : IConflictResolver
    {
        private readonly Window _owner;
        private readonly int _total;
        private ConflictAction? _decided;
        private int _asked;

        public UiConflictResolver(Window owner, int total)
        {
            _owner = owner;
            _total = total;
        }

        public ConflictAction Resolve(ConflictContext context)
        {
            if (_decided.HasValue) { return _decided.Value; }

            _asked++;
            string remaining = _total > 1 ? string.Format("（本批第 {0} 个冲突，共约 {1} 个）", _asked, _total) : string.Empty;

            bool applyToRest;
            ConflictAction action = ConflictDialog.Ask(_owner, context, remaining, out applyToRest);
            if (applyToRest) { _decided = action; }
            return action;
        }
    }
}
