using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HDRSnip.Models;

namespace HDRSnip.Controls;

/// <summary>
/// Click, then press a combination to record it. Replaces hand-editing
/// config.json, which was the only way to change a hotkey before.
/// </summary>
public sealed class HotkeyBox : Button
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(Hotkey),
        typeof(HotkeyBox),
        new FrameworkPropertyMetadata(
            Models.Hotkey.None,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHotkeyChanged));

    private static readonly DependencyPropertyKey IsRecordingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRecording), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public static readonly DependencyProperty IsRecordingProperty = IsRecordingPropertyKey.DependencyProperty;

    public Hotkey Hotkey
    {
        get => (Hotkey)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        private set => SetValue(IsRecordingPropertyKey, value);
    }

    public HotkeyBox()
    {
        Focusable = true;
        UpdateCaption();
    }

    protected override void OnClick()
    {
        base.OnClick();
        IsRecording = true;
        UpdateCaption();
        Keyboard.Focus(this);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsRecording)
        {
            IsRecording = false;
            UpdateCaption();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        // Alt-modified keys arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            IsRecording = false;
            UpdateCaption();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            Hotkey = Models.Hotkey.None;
            IsRecording = false;
            UpdateCaption();
            return;
        }

        if (Models.Hotkey.FromKeyEvent(key, Keyboard.Modifiers) is { } recorded)
        {
            Hotkey = recorded;
            IsRecording = false;
            UpdateCaption();
        }
        // Otherwise a bare modifier is still held down: keep listening.
    }

    private static void OnHotkeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyBox)sender).UpdateCaption();

    private void UpdateCaption() =>
        Content = IsRecording ? "Press a combination…" : Hotkey.ToString();
}
