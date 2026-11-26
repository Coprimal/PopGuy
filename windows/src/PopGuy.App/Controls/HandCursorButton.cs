using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PopGuy.Controls;

/// <summary>A Button with the hand cursor, for clickable items that are not plain buttons (a UIElement's cursor can only be set from a subclass).</summary>
public sealed partial class HandCursorButton : Button
{
    public HandCursorButton() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}
