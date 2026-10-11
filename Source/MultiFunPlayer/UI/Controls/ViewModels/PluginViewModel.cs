using MultiFunPlayer.Common;
using MultiFunPlayer.Plugin;
using PropertyChanged;
using Stylet;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal sealed class PluginViewModel : Conductor<PluginContainer>.Collection.OneActive, IHandle<SettingsMessage>
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IReadOnlyObservableConcurrentCollection<PluginContainer> _source;

    // 插件面板标题栏的展开/收起状态。媒体源、输出目标那些同类属性都会存进配置，
    // 这里原来漏了 —— 所以插件（例如预设脚本）每次启动都是收起状态。
    public bool ContentVisible { get; set; } = true;

    public PluginViewModel(IPluginManager pluginManager, IEventAggregator eventAggregator)
    {
        _source = pluginManager.Containers;
        _source.CollectionChanged += OnSourceCollectionChanged;

        eventAggregator.Subscribe(this);

        OnSourceCollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Handle(SettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            message.Settings["PluginContentVisible"] = ContentVisible;
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (message.Settings.TryGetValue<bool>("PluginContentVisible", out var contentVisible))
                ContentVisible = contentVisible;
        }
    }

    [SuppressPropertyChangedWarnings]
    private void OnSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems?.Count > 1 || e.NewItems?.Count > 1)
            throw new NotSupportedException();

        var oldItem = e.OldItems?.Cast<PluginContainer>().FirstOrDefault();
        var newItem = e.NewItems?.Cast<PluginContainer>().FirstOrDefault();
        var newIndex = MapIndex(e.NewStartingIndex);

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                AddItem(newItem, newIndex);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveItem(oldItem);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveItem(oldItem);
                AddItem(newItem, newIndex == -1 ? MapIndex(_source.IndexOf(newItem)) : newIndex);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveItem(oldItem);
                AddItem(newItem, newIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                // ⚠️ 必须遍历快照：RemoveItem 会修改 Items 本身，
                //    直接 foreach (var item in Items) 会在关闭程序（PluginManager.Dispose → Clear）
                //    时抛 "Collection was modified; enumeration operation may not execute"。
                foreach(var item in Items.ToArray())
                    RemoveItem(item);
                if (Items.Count != 0)
                    throw new UnreachableException();

                Logger.Info("插件标签页：源 {0} 个（{1}）", _source.Count,
                    string.Join("、", _source.Select(x => $"{x.DisplayName}@{(x.View == null ? "无视图" : "有视图")}")));

                foreach(var item in _source)
                    AddItem(item, -1);

                // 按插件自报的 Order 重排（否则顺序 = 目录扫描顺序，"谁先被扫到谁排第一"）
                SortItems();
                ApplyDefaultActiveItem();

                Logger.Info("插件标签页：显示 {0} 个（{1}）", Items.Count,
                    string.Join("、", Items.Select(x => x.DisplayName)));
                break;
        }
    }

    /// <summary>
    /// 标签页实际显示的顺序。
    /// 注意：插件容器**必须留在宿主的 Items 里** —— 它是 Stylet 的 Conductor 集合，
    /// 从 Items 里移除会触发 CloseItem/Dispose（插件被销毁、标签退回文件名），
    /// 所以排序只作用于这个投影集合，绝不动 Items。
    /// </summary>
    public ObservableCollection<PluginContainer> SortedItems { get; } = [];

    /// <summary>
    /// 按插件自报的 Order 排序标签页（Order 相同则按显示名）。
    /// 插件是按目录扫描顺序加载的，不排序的话"谁先被扫到谁在最前面"。
    /// </summary>
    private void SortItems()
    {
        var sorted = Items.OrderBy(x => x.Order)
                          .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        Logger.Info("插件标签排序：{0}", string.Join("、", sorted.Select(x => $"{x.DisplayName}({x.Order})")));

        for (var i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            var current = SortedItems.IndexOf(item);
            if (current == i)
                continue;

            if (current == -1)
                SortedItems.Insert(i, item);
            else
                SortedItems.Move(current, i);
        }

        while (SortedItems.Count > sorted.Count)
            SortedItems.RemoveAt(SortedItems.Count - 1);
    }

    private void AddItem(PluginContainer item, int index)
    {
        item.PropertyChanged -= OnContainerPropertyChanged;
        item.PropertyChanged += OnContainerPropertyChanged;

        // 这里**不能**要求 View 已经就绪：插件的 View 是"标签页被激活"时才创建的，
        // 没视图就不给标签 ⇒ 没标签就不能激活 ⇒ 永远没视图（插件加载成功却在界面上看不到）。
        if (!Items.Contains(item))
            AddItemUnchecked(item, index);
    }

    private void AddItemUnchecked(PluginContainer item, int index)
    {
        if (index == -1)
            Items.Add(item);
        else
            Items.Insert(index++, item);

        SortItems();

        if (ActiveItem == null)
            ActivateItem(item);
    }

    private void RemoveItem(PluginContainer item)
    {
        item.PropertyChanged -= OnContainerPropertyChanged;
        if (Items.Contains(item))
            RemoveItemUnchecked(item);

        SortItems();
    }

    private void RemoveItemUnchecked(PluginContainer item)
    {
        CloseItem(item);
    }

    private int MapIndex(int index)
    {
        if (index == -1)
            return -1;

        while (--index >= 0)
        {
            var ourIndex = Items.IndexOf(_source[index]);
            if (ourIndex >= 0)
                return ourIndex + 1;
        }

        return 0;
    }

    [SuppressPropertyChangedWarnings]
    private void OnContainerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginContainer.View))
        {
            var container = (PluginContainer)sender;
            Logger.Info("插件视图变化：{0}（视图：{1}）", container.DisplayName, container.View == null ? "空" : "已有");

            // 只做"补加"：视图变空只代表插件被停用/重编译，这时把标签页删掉会连锁触发
            // CloseItem → Dispose，把整个插件容器销毁掉（原来就是这样把标签页全弄没的）。
            if (container.View != null && !Items.Contains(container))
                AddItemUnchecked(container, MapIndex(_source.IndexOf(container)));
        }
        else if (e.PropertyName == nameof(PluginContainer.DisplayName))
        {
            // 插件是懒编译的：实例（以及它自报的 Order）要等插件真正编译出来才读得到。
            // 所以插件实例就绪时再排一次序，否则初始排序只能退化成按显示名排（那样 Ayva 又跑到前面）。
            SortItems();
            ApplyDefaultActiveItem();
            OnPropertyChanged(nameof(Items));
        }
    }

    private bool _defaultActiveItemApplied;

    /// <summary>
    /// 把默认选中的标签页设为排序后的第一个（只做一次）。
    /// 必须等所有插件都编译出实例（Order 已知）再定，否则会先选中"临时第一名"。
    /// </summary>
    private void ApplyDefaultActiveItem()
    {
        if (_defaultActiveItemApplied || Items.Count == 0)
            return;

        if (Items.Any(x => x.Order == int.MaxValue))
            return;

        _defaultActiveItemApplied = true;

        var first = SortedItems.Count > 0 ? SortedItems[0] : Items[0];
        if (ActiveItem != first)
            ActiveItem = first;
    }
}
