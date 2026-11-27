using CoreGraphics;
using BubuUI.Elements;
using Foundation;
using UIKit;

namespace BubuUI.Platform.iOS;

// UIKit manages keyboard input, selection and marked text. The BubuUI TextBox paints the editor.
internal sealed class IosTextEditor : UITextView
{
    private TextBox? _client;
    private string _modelText = "";
    private NSRange _modelSelection;
    private bool _updating, _nativeTextChanged, _composing;

    public IosTextEditor(UiSynchronizationContext context) : base(new CGRect(0, 0, 1, 1))
    {
        Alpha = 0;
        AccessibilityElementsHidden = true;
        Changed += (_, _) =>
        {
            using var dispatch = new SceneDispatch(context);
            _nativeTextChanged = true;
            Synchronize(_client);
        };
        ShouldChangeText = (_, _, replacement) =>
        {
            using var dispatch = new SceneDispatch(context);
            if (_client is not { IsReadOnly: false, IsFocused: true } client) return false;
            if (client.AcceptsReturn || replacement != "\n" || MarkedTextRange is not null) return true;
            Close();
            return false;
        };
    }

    public void Open(TextBox client)
    {
        if (_client != client)
        {
            Close();
            _updating = true;
            try
            {
                _client = client;
                SecureTextEntry = client is PasswordBox;
                AutocorrectionType = SecureTextEntry ? UITextAutocorrectionType.No : UITextAutocorrectionType.Default;
                SpellCheckingType = SecureTextEntry ? UITextSpellCheckingType.No : UITextSpellCheckingType.Default;
                AutocapitalizationType = SecureTextEntry ? UITextAutocapitalizationType.None : UITextAutocapitalizationType.Sentences;
                ReturnKeyType = client.AcceptsReturn ? UIReturnKeyType.Default : UIReturnKeyType.Done;
                Text = _modelText = client.Text;
                BecomeFirstResponder();
                SelectedRange = _modelSelection = Selection(client);
                _nativeTextChanged = false;
            }
            finally { _updating = false; }
        }
        else
        {
            Synchronize(client);
            BecomeFirstResponder();
        }
    }

    public void Close()
    {
        if (_client is not { } client) return;
        _client = null;
        CancelComposition(client);
        ResignFirstResponder();
        Text = _modelText = "";
        UndoManager?.RemoveAllActions();
    }

    public void Synchronize(Element? focused)
    {
        if (_updating || _client is not { } client) return;
        if (client != focused || !client.IsFocused || client.IsDisposed || client.IsReadOnly ||
            !client.IsEffectiveEnabled || !client.IsEffectivelyVisible)
        {
            Close();
            return;
        }
        _updating = true;
        try
        {
            if (_modelText != client.Text || !_modelSelection.Equals(Selection(client)))
            {
                // A binding or pointer changed the model while the keyboard was open.
                CancelComposition(client);
                if (MarkedTextRange is not null) UnmarkText();
                if (Text != client.Text) Text = client.Text;
                SelectedRange = Selection(client);
            }
            else
            {
                if ((_nativeTextChanged || _composing) && MarkedTextRange is { } marked)
                {
                    if (!_composing) client.RaiseEvent(new TextCompositionEventArgs("", CompositionStage.Started));
                    _composing = true;
                    var preedit = TextInRange(marked) ?? "";
                    if (client.CompositionText != preedit)
                        client.RaiseEvent(new TextCompositionEventArgs(preedit, CompositionStage.Updated));
                    return;
                }
                if (_nativeTextChanged || _composing) ApplyNativeText(client);
                var selection = SelectedRange;
                if (selection.Location >= 0 && selection.Location <= client.Text.Length &&
                    selection.Length >= 0 && selection.Length <= client.Text.Length - selection.Location &&
                    !selection.Equals(Selection(client)))
                    client.Select((int)selection.Location, (int)selection.Length);
            }
            _modelText = client.Text;
            _modelSelection = Selection(client);
        }
        finally
        {
            _nativeTextChanged = false;
            _updating = false;
        }
    }

    private void ApplyNativeText(TextBox client)
    {
        var next = Text ?? "";
        if (next == client.Text)
        {
            CancelComposition(client);
            return;
        }
        var selection = (client.SelectionStart, client.SelectionLength);
        var edit = MobileTextEdit.GetReplacement(client.Text, next, selection);
        if ((edit.Start, edit.Length) != selection) client.Select(edit.Start, edit.Length);
        if (_composing)
        {
            _composing = false;
            client.RaiseEvent(new TextCompositionEventArgs(edit.Replacement, CompositionStage.Committed));
        }
        else client.InsertText(edit.Replacement);
        // MaxLength or single-line filtering may have changed the accepted replacement.
        if (client.Text != next)
        {
            Text = client.Text;
            SelectedRange = Selection(client);
        }
    }

    private void CancelComposition(TextBox client)
    {
        if (_composing && !client.IsDisposed)
            client.RaiseEvent(new TextCompositionEventArgs("", CompositionStage.Cancelled));
        _composing = false;
    }

    private static NSRange Selection(TextBox client) => new(client.SelectionStart, client.SelectionLength);
}
