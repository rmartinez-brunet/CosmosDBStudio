﻿using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using CosmosDBStudio.Model;
using CosmosDBStudio.Services;
using EssentialMVVM;
using Hamlet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDBStudio.ViewModel
{
    public class QuerySheetViewModel : BindableBase
    {
        private static int _untitledCounter;

        private readonly int _untitledNumber;
        private readonly IContainerContext _containerContext;
        private readonly IViewModelFactory _viewModelFactory;

        public QuerySheetViewModel(
            IContainerContext containerContext,
            IViewModelFactory viewModelFactory,
            QuerySheet querySheet,
            string? path)
        {
            _containerContext = containerContext;
            _viewModelFactory = viewModelFactory;

            _filePath = path;
            _untitledCounter = string.IsNullOrEmpty(path)
                ? ++_untitledCounter
                : 0;
            _text = querySheet.Text;
            _result = _viewModelFactory.CreateNotRunQueryResultViewModel();

            PartitionKey = querySheet.PartitionKey;
            PartitionKeyMRU = new ObservableCollection<string>();

            Parameters = new ObservableCollection<ParameterViewModel>();
            foreach (var (name, value) in querySheet.Parameters)
            {
                var p = new ParameterViewModel { Name = name, RawValue = value };
                p.DeleteRequested += OnParameterDeleteRequested;
                Parameters.Add(p);
            }
            AddParameterPlaceholder();
            
            Errors = new ViewModelValidator<QuerySheetViewModel>(this);
            Errors.AddValidator(
                vm => vm.PartitionKey,
                value => TryParsePartitionKeyValue(value, out _)
                    ? null
                    : "Invalid partition key value");
        }

        public ViewModelValidator<QuerySheetViewModel> Errors { get; }

        public string Title => string.IsNullOrEmpty(FilePath)
                ? $"Untitled {++_untitledCounter}"
                : Path.GetFileNameWithoutExtension(FilePath);

        private string? _filePath;
        public string? FilePath
        {
            get => _filePath;
            set => Set(ref _filePath, value).AndNotifyPropertyChanged(nameof(Title));
        }

        private string _text;
        public string Text
        {
            get => _text;
            set => Set(ref _text, value)
                .AndRaiseCanExecuteChanged(_executeCommand);
        }

        public string ContainerPath => $"{_containerContext.AccountName}/{_containerContext.DatabaseId}/{_containerContext.ContainerId}";

        private string _selectedText = string.Empty;
        public string SelectedText
        {
            get => _selectedText;
            set => Set(ref _selectedText, value)
                .AndRaiseCanExecuteChanged(_executeCommand);
        }

        private (int start, int end) _selection;
        public (int start, int end) Selection
        {
            get => _selection;
            set => Set(ref _selection, value);
        }

        private int _cursorPosition;
        public int CursorPosition
        {
            get => _cursorPosition;
            set => Set(ref _cursorPosition, value)
                .AndRaiseCanExecuteChanged(_executeCommand);
        }

        public ObservableCollection<string> PartitionKeyMRU { get; }

        private string? _partitionKey;
        public string? PartitionKey
        {
            get => _partitionKey;
            set => Set(ref _partitionKey, value).AndExecute(() => Errors?.Refresh());
        }

        public ObservableCollection<ParameterViewModel> Parameters { get; }

        private QueryResultViewModelBase _result;
        public QueryResultViewModelBase Result
        {
            get => _result;
            set => Set(ref _result, value);
        }

        private AsyncDelegateCommand? _executeCommand;
        public ICommand ExecuteCommand => _executeCommand ??= new AsyncDelegateCommand(ExecuteAsync, CanExecute);

        private bool _showParameters;
        public bool ShowParameters
        {
            get => _showParameters;
            set => Set(ref _showParameters, value);
        }

        public QuerySheet GetQuerySheet()
        {
            var querySheet =  new QuerySheet
            {
                AccountId = _containerContext.AccountId,
                DatabaseId = _containerContext.DatabaseId,
                ContainerId = _containerContext.ContainerId,
                Text = Text,
                PartitionKey = PartitionKey
            };

            foreach (var p in Parameters)
            {
                if (p.Name is string name)
                {
                    querySheet.Parameters[name] = p.RawValue;
                }
            }

            return querySheet;
        }

        private bool CanExecute()
        {
            return !Errors.HasError &&
                (!string.IsNullOrEmpty(SelectedText) ||
                 !string.IsNullOrEmpty(ExtendSelectionAroundCursor(false)));
        }

        private async Task ExecuteAsync()
        {
            var queryText = SelectedText;
            if (string.IsNullOrEmpty(queryText))
                queryText = ExtendSelectionAroundCursor(true);

            if (string.IsNullOrEmpty(queryText))
                return;

            // TODO: options

            if (TryParsePartitionKeyValue(PartitionKey, out Option<object?> partitionKey) &&
                !string.IsNullOrEmpty(PartitionKey))
            {
                PushMRU(PartitionKeyMRU, PartitionKey!);
            }

            var query = new Query(queryText);
            query.PartitionKey = partitionKey;
            foreach (var p in Parameters)
            {
                if (p.IsPlaceholder || p.Errors.HasError)
                    continue;

                string name = p.Name!;
                string nakedName;
                if (name.StartsWith('@'))
                {
                    nakedName = name.Substring(1);
                }
                else
                {
                    nakedName = name;
                    name = "@" + name;
                }

                if (!Regex.IsMatch(queryText, $@"@\b{nakedName}\b", RegexOptions.Multiline))
                    continue;

                p.TryParseParameterValue(p.RawValue, out object? value);
                query.Parameters[name] = value;
                p.PushMRU(p.RawValue!);
            }
            var result = await _containerContext.Query.ExecuteAsync(query, default);
            Result = _viewModelFactory.CreateQueryResultViewModel(result, _containerContext);
        }

        private static void PushMRU(ObservableCollection<string> mruList, string value)
        {
            int index = mruList.IndexOf(value);
            if (index >= 0)
            {
                mruList.Move(index, 0);
            }
            else
            {
                mruList.Insert(0, value);
            }
            
            while (mruList.Count > 10)
            {
                mruList.RemoveAt(mruList.Count - 1);
            }
        }

        private static readonly string QuerySeparator = Environment.NewLine + Environment.NewLine;
        private string ExtendSelectionAroundCursor(bool applySelectionChange)
        {
            if (string.IsNullOrEmpty(Text))
                return string.Empty;

            var position = CursorPosition;
            if (position > Text.Length)
                position = Text.Length;

            var previousSeparator = Text.LastIndexOf(QuerySeparator, position, position);
            if (previousSeparator < 0)
                previousSeparator = 0;

            var nextSeparator = Text.IndexOf(QuerySeparator, position);
            if (nextSeparator < 0)
                nextSeparator = Text.Length - 1;

            int start = ForceIndexInRange(previousSeparator);
            while (char.IsWhiteSpace(Text[start]) && start + 1 < Text.Length)
                start++;

            int end = ForceIndexInRange(nextSeparator);
            while (char.IsWhiteSpace(Text[end]) && end - 1 >= 0)
                end--;

            if (start > end)
                return string.Empty;

            string queryText = Text.Substring(start, end - start + 1);
            //string queryText = Text.Substring(previousSeparator, nextSeparator - previousSeparator).Trim();
            if (queryText.Contains(QuerySeparator))
            {
                // We have two queries; the cursor was probably in the middle of the separator
                return string.Empty;
            }

            if (applySelectionChange)
            {
                Selection = (start, end - start + 1);
            }

            return queryText;

            int ForceIndexInRange(int index)
            {
                if (index < 0)
                    return 0;
                if (index >= Text.Length)
                    return Text.Length - 1;
                return index;
            }
        }

        private DelegateCommand? _closeCommand;
        public ICommand CloseCommand => _closeCommand ??= new DelegateCommand(Close);

        private void Close()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? CloseRequested;

        private bool TryParsePartitionKeyValue(string? rawValue, out Option<object?> value)
        {
            if (string.IsNullOrEmpty(rawValue))
            {
                value = Option.None();
                return true;
            }

            try
            {
                using var tReader = new StringReader(rawValue);
                using var jReader = new JsonTextReader(tReader)
                {
                    DateParseHandling = DateParseHandling.None
                };

                var token = JValue.ReadFrom(jReader);
                value = Option.Some(token.ToObject<object?>());
                return true;
            }
            catch
            {
                value = Option.None();
                return false;
            }
        }

        private void AddParameterPlaceholder()
        {
            var placeholder = new ParameterViewModel { IsPlaceholder = true };
            placeholder.Created += OnParameterCreated;
            Parameters.Add(placeholder);
        }

        private void OnParameterCreated(object? sender, EventArgs _)
        {
            if (sender is ParameterViewModel placeholder)
            {
                placeholder.Created -= OnParameterCreated;
                placeholder.DeleteRequested += OnParameterDeleteRequested;
                AddParameterPlaceholder();
            }
        }

        private void OnParameterDeleteRequested(object? sender, EventArgs e)
        {
            if (sender is ParameterViewModel parameter)
            {
                Parameters.Remove(parameter);
            }
        }
    }
}