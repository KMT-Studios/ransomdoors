using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace rans0m;

/// <summary>
/// Attached behavior restricting a <see cref="TextBox"/> to digit input.
/// </summary>
public static class NumericBehavior
{
    [AttachedPropertyBrowsableForType(typeof(TextBox))]
    [Category("NumericBehavior")]
    [Description("Restricts TextBox input to numeric values only")]
    [DefaultValue(false)]
    public static bool GetNumericOnly(DependencyObject obj) => (bool)obj.GetValue(NumericOnlyProperty);

    public static void SetNumericOnly(DependencyObject obj, bool value) => obj.SetValue(NumericOnlyProperty, value);

    public static readonly DependencyProperty NumericOnlyProperty =
        DependencyProperty.RegisterAttached(
            "NumericOnly",
            typeof(bool),
            typeof(NumericBehavior),
            new UIPropertyMetadata(false, OnNumericOnlyChanged));

    private static void OnNumericOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox)
        {
            return;
        }

        if (e.NewValue is true)
        {
            textBox.PreviewTextInput += OnPreviewTextInput;
        }
        else
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }
}