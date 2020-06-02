using Microsoft.AspNetCore.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using System.Reactive;
using BlazorFluentUI.Style;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorFluentUI
{
    public class BFUList<TItem> : BFUComponentBase, IDisposable, IHasPreloadableGlobalStyle
    {
        //protected bool firstRender = false;

        protected const int DEFAULT_ITEMS_PER_PAGE = 10;
        protected const int DEFAULT_RENDERED_WINDOWS_BEHIND = 2;
        protected const int DEFAULT_RENDERED_WINDOWS_AHEAD = 2;

        private double thresholdChangePercent = 0.10;

        //protected ElementReference rootDiv;
        protected ElementReference surfaceDiv;
        protected ElementReference contentDiv;

        private double _averagePageHeight = 100;
        private bool isFirstRender = true;
        private bool _shouldRender = false;

        private int numItemsToSkipBefore;
        private int numItemsToShow;
        private double averageHeight = 50;


        //private int minRenderedPage;
        //private int maxRenderedPage;
        private ElementMeasurements _lastScrollRect = new ElementMeasurements();
        private ElementMeasurements _scrollRect = new ElementMeasurements();
        //private double _scrollHeight;
        private Rectangle surfaceRect = new Rectangle();
        private double _height;
        public double CurrentHeight => _height;
        private bool _jsAvailable = false;

        //private object _lastVersion = null;

        [Inject] 
        private IJSRuntime JSRuntime { get; set; }

        [Parameter] 
        public object Data { get; set; }

        [Parameter] 
        public Func<int, Rectangle, int> GetItemCountForPage { get; set; }

        //[Parameter] 
        //public EventCallback<ItemContainer<TItem>> ItemClicked { get; set; }

        [Parameter] 
        public bool ItemFocusable { get; set; } = false;

        [Parameter] 
        public IEnumerable<TItem> ItemsSource { get; set; }

        [Parameter] 
        public RenderFragment<ItemContainer<TItem>> ItemTemplate { get; set; }

        [Parameter] 
        public EventCallback<(double, object)> OnListScrollerHeightChanged { get; set; }

        [Parameter]
        public EventCallback<Viewport> OnViewportChanged { get; set; }

        //[Parameter] public BFUSelection<TItem> Selection { get; set; }
        //[Parameter] public EventCallback<BFUSelection<TItem>> SelectionChanged { get; set; }
        //[Parameter] public SelectionMode SelectionMode { get; set; } = SelectionMode.Single;
        //[Parameter]
        //public bool UseDefaultStyling { get; set; } = true;


        //[Parameter] public bool UseInternalScrolling { get; set; } = true;
        
       

        private IEnumerable<TItem> _itemsSource;

        protected RenderFragment ItemPagesRender { get; set; }

        private ISubject<(int index, double height)> pageMeasureSubject = new Subject<(int index, double height)>();
        private IDisposable _heightSub;

        private ISubject<Unit> _scrollSubject = new Subject<Unit>();
        private IDisposable _scrollSubscription;

        private ISubject<Unit> _scrollDoneSubject = new Subject<Unit>();
        private IDisposable _scrollDoneSubscription;

        private List<BFUListPage<TItem>> renderedPages = new List<BFUListPage<TItem>>();

        private List<TItem> selectedItems = new List<TItem>();
        private string _resizeRegistration;

        private Dictionary<int, double> _pageSizes = new Dictionary<int, double>();
        private bool _needsRemeasure = true;

        private Viewport _viewport = new Viewport();
        private ElementMeasurements _surfaceRect = new ElementMeasurements();

        //private IDisposable _updatesSubscription;

        //private ICollection<Rule> ListRules { get; set; } = new System.Collections.Generic.List<Rule>();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<BFUGlobalCS>(0);
            builder.AddAttribute(1, "Component", this);
            builder.AddAttribute(2, "CreateGlobalCss", new System.Func<ICollection<IRule>>(() => CreateGlobalCss(Theme)));
            builder.CloseComponent();
            
            // Render actual content
            builder.OpenElement(3, "div");
            builder.AddAttribute(4, "class", $"ms-List mediumFont {ClassName}");
            builder.AddAttribute(5, "role", "list");
            builder.AddAttribute(6, "style", $"{Style}overflow-y:hidden;height:100%;");
            builder.AddElementReferenceCapture(7, (element) => RootElementReference = element);

            builder.OpenElement(11, "div");
            builder.AddAttribute(12, "class", $"ms-List-surface");
            builder.AddAttribute(13, "role", "presentation");
            builder.AddAttribute(14, "style", $"overflow-y:auto;height:100%;");
            builder.AddElementReferenceCapture(15, (element) => surfaceDiv = element);

            builder.OpenElement(21, "div");
            var translateY = numItemsToSkipBefore * averageHeight;
            builder.AddAttribute(22, "style", $"transform: translateY({ translateY }px);");
            builder.AddAttribute(23, "data-translateY", translateY);
            builder.AddAttribute(24, "role", "presentation");
            builder.AddAttribute(25, "class", "ms-List-viewport");
            builder.AddElementReferenceCapture(26, (element) => contentDiv = element);

            builder.OpenRegion(27);
            int index = 0;
            foreach (var item in ItemsSource.Skip(numItemsToSkipBefore).Take(numItemsToShow))
            {
                index++;
                builder.OpenElement(30 + (index*2), "div");
                builder.AddAttribute(31 + (index*2), "data-index", numItemsToSkipBefore + index);
                ItemTemplate(new ItemContainer<TItem>() {Index = numItemsToSkipBefore + index, Item=item })(builder);
                builder.CloseElement();
            }
            builder.CloseRegion();

            builder.CloseElement();

            // Also emit a spacer that causes the total vertical height to add up to Items.Count()*numItems
            builder.OpenElement(32 + numItemsToShow * 2, "div");
            var numHiddenItems = ItemsSource.Count() - numItemsToShow;
            builder.AddAttribute(32 + numItemsToShow * 2 + 1, "style", $"width: 1px; height: { numHiddenItems * averageHeight }px;");
            builder.CloseElement();

            builder.CloseElement();

            builder.CloseElement();

            

            
        }

        protected RenderFragment<RenderFragment<ItemContainer<TItem>>> ItemContainer { get; set; }

        protected override Task OnInitializedAsync()
        {


            return base.OnInitializedAsync();
        }

        protected override async Task OnParametersSetAsync()
        {

            if (_itemsSource != ItemsSource)
            {
                if (this._itemsSource is System.Collections.Specialized.INotifyCollectionChanged)
                {
                    (this._itemsSource as System.Collections.Specialized.INotifyCollectionChanged).CollectionChanged -= ListBase_CollectionChanged;
                }

                _itemsSource = ItemsSource;

                if (this.ItemsSource is System.Collections.Specialized.INotifyCollectionChanged)
                {
                    (this.ItemsSource as System.Collections.Specialized.INotifyCollectionChanged).CollectionChanged += ListBase_CollectionChanged;
                }
                
                _shouldRender = true;
                _needsRemeasure = true;
            }

            //CreateCss();
            await base.OnParametersSetAsync();
        }


        private void ListBase_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {            
            _shouldRender = true;
            InvokeAsync(StateHasChanged);
        }

        public ICollection<IRule> CreateGlobalCss(ITheme theme)
        {
            var listRules = new HashSet<IRule>();
            //creates a method that pulls in focusstyles the way the react controls do it.
            var focusStyleProps = new FocusStyleProps(theme);
            var mergeStyleResults = FocusStyle.GetFocusStyle(focusStyleProps, ".ms-List-cell-default");

            listRules.Clear();
            // Cell only
            listRules.Add(new Rule()
            {
                Selector = new CssStringSelector() { SelectorName = ".ms-List-cell-default" },
                Properties = new CssString()
                {
                    Css = $"padding-top:11px;" +
                          $"padding-bottom:11px;" +
                          $"min-height:42px;" +
                          $"min-width:100%;" +
                          $"overflow:hidden;" +
                          $"box-sizing:border-box;" +
                          $"border-bottom:1px solid {theme.Palette.NeutralLighter};" +
                          $"display:inline-flex;"
                          +
                          mergeStyleResults.MergeRules
                }
            });
            listRules.Add(new Rule()
            {
                Selector = new CssStringSelector() { SelectorName = ".ms-List-cell-default:hover" },
                Properties = new CssString()
                {
                    Css = $"background-color:{theme.Palette.NeutralLighter};" 
                }
            });
            listRules.Add(new Rule()
            {
                Selector = new CssStringSelector() { SelectorName = ".ms-List-cell-default.is-selected" },
                Properties = new CssString()
                {
                    Css = $"background-color:{theme.Palette.NeutralLight};"
                }
            });

            foreach (var rule in mergeStyleResults.AddRules)
                listRules.Add(rule);

            return listRules;
        }

        public void ForceUpdate()
        {
            MeasureContainerAsync();
        }

        private RenderFragment RenderPages(int startPage, int endPage, double leadingPadding = 0) => builder =>
          {
              try
              {
                  renderedPages.Clear();

                  builder.OpenComponent(0, typeof(BFUListVirtualizationSpacer));
                  builder.AddAttribute(1, "Height", _averagePageHeight * startPage);
                  builder.CloseComponent();

                  const int lineCount = 11;
                  var totalItemsRendered = 0;
                  for (var i = startPage; i <= endPage; i++)
                  {
                      builder.OpenComponent(i * lineCount + 2, typeof(BFUListPage<TItem>));
                      builder.AddAttribute(i * lineCount + 3, "ItemTemplate", ItemTemplate);
                      if (GetItemCountForPage != null)
                      {
                          totalItemsRendered += GetItemCountForPage(i, surfaceRect);
                          builder.AddAttribute(i * lineCount + 4, "ItemsSource", ItemsSource.Skip(i * GetItemCountForPage(i, surfaceRect)).Take(GetItemCountForPage(i, surfaceRect)));
                          builder.AddAttribute(i * lineCount + 5, "StartIndex", i * GetItemCountForPage(i, surfaceRect));
                      }
                      else
                      {
                          totalItemsRendered += DEFAULT_ITEMS_PER_PAGE;
                          builder.AddAttribute(i * lineCount + 4, "ItemsSource", ItemsSource.Skip(i * DEFAULT_ITEMS_PER_PAGE).Take(DEFAULT_ITEMS_PER_PAGE));
                          builder.AddAttribute(i * lineCount + 5, "StartIndex", i * DEFAULT_ITEMS_PER_PAGE);
                      }
                      builder.AddAttribute(i * lineCount + 6, "PageMeasureSubject", pageMeasureSubject);
                      builder.AddComponentReferenceCapture(i * lineCount + 11, (comp) => renderedPages.Add((BFUListPage<TItem>)comp));

                      builder.CloseComponent();
                  }

                  int totalPages = 0;
                  if (GetItemCountForPage != null)
                      totalPages = (int)Math.Ceiling(ItemsSource.Count() / (double)GetItemCountForPage(0, null));
                  else
                      totalPages = (int)Math.Ceiling(ItemsSource.Count() / (double)DEFAULT_ITEMS_PER_PAGE);
                  builder.OpenComponent(totalPages * lineCount, typeof(BFUListVirtualizationSpacer));
                  builder.AddAttribute(totalPages * lineCount + 1, "Height", _averagePageHeight * (totalPages - endPage - 1));
                  builder.CloseComponent();
              }
              catch (Exception ex)
              {
                  Debug.WriteLine(ex.ToString());
              }

          };

        

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                var objectRef = DotNetObjectReference.Create(this);
                var initResult = await JSRuntime.InvokeAsync<ScrollEventArgs>("BlazorFluentUiList.initialize", objectRef, surfaceDiv, contentDiv);
                OnScroll(initResult);
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        private async Task MeasureContainerAsync()
        {
            _surfaceRect = await this.JSRuntime.InvokeAsync<ElementMeasurements>("BlazorFluentUiList.measureElementRect", this.surfaceDiv);
            //var oldScrollHeight = _scrollHeight;
            _lastScrollRect = _scrollRect;
            _scrollRect = await this.JSRuntime.InvokeAsync<ElementMeasurements>("BlazorFluentUiList.measureScrollWindow", this.surfaceDiv);

            //_scrollHeight = await JSRuntime.InvokeAsync<double>("BlazorFluentUiBaseComponent.getScrollHeight", this.surfaceDiv);
            surfaceRect = new Rectangle(_surfaceRect.left, _surfaceRect.width, _surfaceRect.top, _surfaceRect.height);

            if (_height != surfaceRect.height)
            {
                _height = surfaceRect.height;
                _shouldRender = true;
                StateHasChanged();
            }

            
            if (_lastScrollRect.height != _scrollRect.height)
                await OnListScrollerHeightChanged.InvokeAsync((_scrollRect.height, Data));
        }

        private async void HandleListScrollerHeightChanged(object sender, double height)
        {
            Debug.WriteLine($"Height changed: {height}");
            await MeasureContainerAsync();
            
        }

        [JSInvokable]
        public async void ResizeHandler(double width, double height)
        {
            await MeasureContainerAsync();

            _viewport.Height = _surfaceRect.cheight;
            _viewport.Width = _surfaceRect.cwidth;
            _viewport.ScrollHeight = _scrollRect.height;
            _viewport.ScrollWidth = _scrollRect.width;
            await OnViewportChanged.InvokeAsync(_viewport);
        }


        [JSInvokable]
        public void OnScroll(ScrollEventArgs args)
        {
            averageHeight = args.AverageHeight;
            // TODO: Support horizontal scrolling too
            var relativeTop = args.ContainerRect.Top - args.ContentRect.Top;
            numItemsToSkipBefore = Math.Max(0, (int)(relativeTop / averageHeight));

            var visibleHeight = args.ContainerRect.Bottom - (args.ContentRect.Top + numItemsToSkipBefore * averageHeight);
            numItemsToShow = (int)Math.Ceiling(visibleHeight / averageHeight) + 3;

            StateHasChanged();
        }


        public async void Dispose()
        {
            //if (OnListScrollerHeightChanged.HasDelegate)
            //    await OnListScrollerHeightChanged.InvokeAsync((0, Data));
            _heightSub?.Dispose();
            _scrollSubscription?.Dispose();

            //_updatesSubscription?.Dispose();
            if (_itemsSource is System.Collections.Specialized.INotifyCollectionChanged)
            {
                (_itemsSource as System.Collections.Specialized.INotifyCollectionChanged).CollectionChanged -= ListBase_CollectionChanged;
            }
            if (_resizeRegistration != null)
            {
                await JSRuntime.InvokeVoidAsync("BlazorFluentUiBaseComponent.deregisterResizeEvent", _resizeRegistration);
            }
            Debug.WriteLine("List was disposed");
        }

        public class ScrollEventArgs
        {
            public DOMRect ContainerRect { get; set; }
            public DOMRect ContentRect { get; set; }

            public double AverageHeight { get; set; }
        }

        public class DOMRect
        {
            public double Top { get; set; }
            public double Bottom { get; set; }
            public double Left { get; set; }
            public double Right { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }
    }
}
