// This app references both WPF and WinForms (for the tray icon), so several common
// type names exist in two namespaces. Pin the WPF versions globally.
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using FontFamily = System.Windows.Media.FontFamily;
global using Point = System.Windows.Point;
global using MessageBox = System.Windows.MessageBox;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using CheckBox = System.Windows.Controls.CheckBox;
global using TextBox = System.Windows.Controls.TextBox;
global using Button = System.Windows.Controls.Button;
global using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
global using Orientation = System.Windows.Controls.Orientation;
global using UserControl = System.Windows.Controls.UserControl;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using DragEventArgs = System.Windows.DragEventArgs;
global using Cursor = System.Windows.Input.Cursor;
global using Cursors = System.Windows.Input.Cursors;
global using DataObject = System.Windows.DataObject;
global using DragDropEffects = System.Windows.DragDropEffects;
