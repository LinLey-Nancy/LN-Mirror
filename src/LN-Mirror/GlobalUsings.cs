// 工程同时使用 WPF 与 WinForms（托盘图标）。WinForms 会引入 System.Windows.Forms / System.Drawing，
// 与 WPF 的 Application、MessageBox、Color 等同名类型冲突，这里用全局别名一次性消歧。
global using System.IO;
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using Clipboard = System.Windows.Clipboard;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using SolidColorBrush = System.Windows.Media.SolidColorBrush;
global using Key = System.Windows.Input.Key;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using ModifierKeys = System.Windows.Input.ModifierKeys;
global using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;