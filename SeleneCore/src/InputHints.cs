using System;
using System.Windows;
using System.Windows.Input;

namespace Selene
{
    public enum InputKind { Controller, Keyboard, Mouse }
    public static class InputHints
    {
        public static InputKind Kind { get; private set; }
        public static event Action Changed;
        private static Point mouse;
        private static bool haveMouse;
        public static void Set(InputKind kind)
        {
            if (Kind == kind) return;
            Kind = kind;
            Refresh();
        }
        public static void Refresh() { var changed = Changed; if (changed != null) changed(); }
        public static void Observe(InputEventArgs input)
        {
            var key = input as KeyEventArgs;
            if (key != null)
            {
                if (key.GetType().Name == "GameControllerInputEventArgs") Set(InputKind.Controller);
                else if (!Ui.KeyIsPadCopy() && !Ui.PressFromPad && (key.RoutedEvent == Keyboard.KeyDownEvent || key.RoutedEvent == Keyboard.PreviewKeyDownEvent)) Set(InputKind.Keyboard);
                return;
            }
            var pointer = input as MouseEventArgs;
            if (pointer == null) return;
            if (pointer is MouseButtonEventArgs || pointer is MouseWheelEventArgs) { Set(InputKind.Mouse); return; }
            var window = Application.Current != null ? Application.Current.MainWindow : null;
            if (window == null) return;
            PointerMoved(pointer.GetPosition(window));
        }
        public static void PointerMoved(Point position)
        {
            if (!haveMouse) { mouse = position; haveMouse = true; return; }
            if (Math.Abs(position.X - mouse.X) > 3 || Math.Abs(position.Y - mouse.Y) > 3) { mouse = position; lastPointerMove = DateTime.Now; Set(InputKind.Mouse); }
        }

        // the pointer really moved lately (a mouse, or a controller turned into one)
        private static DateTime lastPointerMove = DateTime.MinValue;
        public static bool PointerMovedWithin(int ms) { return (DateTime.Now - lastPointerMove).TotalMilliseconds < ms; }
        public static string Label(string button)
        {
            if (Kind == InputKind.Controller) return null;
            if (Kind == InputKind.Mouse && button == "A") return "Click";
            switch (button)
            {
                case "A": return "Enter";
                case "B": return "Esc";
                case "X": return "Del";
                case "Y": return "F2";
                case "LB": return "PgUp";
                case "RB": return "PgDn";
                case "LT": return "−";
                case "RT": return "+";
                case "RS": case "RightStick": return "F3";
                case "Start": return "Tab";
                case "View": return "V";
                default: return button;
            }
        }
    }
}
