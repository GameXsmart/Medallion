// Both WPF and WinForms are referenced: WPF for the interface, WinForms purely for the
// tray icon, which has no first-class WPF equivalent. That makes a dozen type names
// ambiguous, so the WPF meaning is fixed here once for the whole project. Files that
// genuinely need the WinForms type (Tray/TrayIconHost.cs) qualify it locally.

global using Application = System.Windows.Application;
global using UserControl = System.Windows.Controls.UserControl;
global using MessageBox = System.Windows.MessageBox;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using FontFamily = System.Windows.Media.FontFamily;
global using Image = System.Windows.Controls.Image;
global using TextBox = System.Windows.Controls.TextBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Cursor = System.Windows.Input.Cursor;
global using Cursors = System.Windows.Input.Cursors;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using Binding = System.Windows.Data.Binding;
global using Clipboard = System.Windows.Clipboard;
global using DataObject = System.Windows.DataObject;
global using DragEventArgs = System.Windows.DragEventArgs;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
