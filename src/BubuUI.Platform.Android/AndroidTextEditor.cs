using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using BubuUI.Elements;

namespace BubuUI.Platform.Android;

// Android owns the editable buffer (including IME composing spans); BubuUI paints its contents.
internal sealed class AndroidTextEditor : EditText
{
    private TextBox? _client;
    private bool _updating;
    private (int Start, int End) _lastSelection;

    public AndroidTextEditor(Context context) : base(context)
    {
        Alpha = 0;
        Visibility = ViewStates.Gone;
        ImportantForAccessibility = ImportantForAccessibility.No;
        FocusableInTouchMode = true;
        TextChanged += (_, _) => ApplyNativeText();
    }

    private InputMethodManager? Keyboard =>
        Context!.GetSystemService(Context.InputMethodService) as InputMethodManager;

    public void Open(TextBox client)
    {
        if (_client != client)
        {
            _updating = true;
            try
            {
                _client = client;
                SetSingleLine(!client.AcceptsReturn);
                InputType = InputTypes.ClassText |
                    (client.AcceptsReturn ? InputTypes.TextFlagMultiLine : 0) |
                    (client is PasswordBox ? InputTypes.TextVariationPassword | InputTypes.TextFlagNoSuggestions : 0);
                ImeOptions = client.AcceptsReturn ? ImeAction.None : ImeAction.Done;
                Text = client.Text;
                Visibility = ViewStates.Visible;
                RequestFocus();
                SetModelSelection(client);
            }
            finally { _updating = false; }
            Keyboard?.RestartInput(this);
        }
        else SetModelSelection(client);
        Keyboard?.ShowSoftInput(this, ShowFlags.Implicit);
    }

    public void EndEditing()
    {
        if (_client is null) return;
        _client = null;
        Keyboard?.HideSoftInputFromWindow(WindowToken, HideSoftInputFlags.None);
        ClearFocus();
        Visibility = ViewStates.Gone;
        Text = "";
    }

    public void Synchronize(Element? focused)
    {
        if (_client is not { } client) return;
        if (client != focused || client.IsDisposed || client.IsReadOnly || !client.IsEffectiveEnabled || !client.IsEffectivelyVisible)
        {
            EndEditing();
            return;
        }
        if (_updating) return;
        _updating = true;
        try
        {
            if (Text != client.Text)
            {
                Text = client.Text;
                SetModelSelection(client);
                Keyboard?.RestartInput(this);
            }
            else if (ModelSelection(client) != _lastSelection)
                SetModelSelection(client);
            else if (SelectionStart >= 0 && SelectionEnd >= 0 &&
                     (Math.Min(SelectionStart, SelectionEnd) != client.SelectionStart ||
                      Math.Abs(SelectionEnd - SelectionStart) != client.SelectionLength))
            {
                client.Select(Math.Min(SelectionStart, SelectionEnd), Math.Abs(SelectionEnd - SelectionStart));
                _lastSelection = ModelSelection(client);
                // Preserve the native selection direction until BubuUI changes the selection.
            }
        }
        finally { _updating = false; }
    }

    private static (int Start, int End) ModelSelection(TextBox client) =>
        (client.CaretIndex == client.SelectionStart ? client.SelectionStart + client.SelectionLength : client.SelectionStart,
         client.CaretIndex);

    private void SetModelSelection(TextBox client)
    {
        _lastSelection = ModelSelection(client);
        SetSelection(_lastSelection.Start, _lastSelection.End);
    }

    private void ApplyNativeText()
    {
        if (_updating || _client is not { IsReadOnly: false } client) return;
        var next = Text ?? "";
        var old = client.Text;
        if (old == next) return;
        var edit = MobileTextEdit.GetReplacement(old, next);
        _updating = true;
        try
        {
            client.Select(edit.Start, edit.Length);
            client.InsertText(edit.Replacement);
            _lastSelection = ModelSelection(client);
        }
        finally { _updating = false; }
    }
}
