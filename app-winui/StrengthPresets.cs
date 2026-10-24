using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static NoiseReduction.WinUI.MicrophoneText;

namespace NoiseReduction.WinUI
{
    // Три пресета силы, точное значение в дБ остаётся за ними
    internal sealed class StrengthPresets
    {
        internal static readonly int[] Values = { 12, 24, 40 };
        private static readonly string[] Names = { "Мягко", "Обычно", "Сильно" };
        private readonly ToggleButton[] buttons = new ToggleButton[3];
        private bool updating;
        internal readonly Grid View = new Grid { ColumnSpacing = 4 };
        internal event Action<int> Selected;

        internal StrengthPresets()
        {
            for (int index = 0; index < buttons.Length; index++)
            {
                int value = Values[index];
                var button = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
                button.Click += (_, _) => { if (!updating) Selected?.Invoke(value); };
                View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(button, index); View.Children.Add(button);
                buttons[index] = button;
            }
        }

        internal void Localize() { for (int index = 0; index < buttons.Length; index++) buttons[index].Content = T(Names[index]); }

        internal void Update(int value, bool enabled)
        {
            updating = true;
            for (int index = 0; index < buttons.Length; index++) { buttons[index].IsChecked = Values[index] == value; buttons[index].IsEnabled = enabled; }
            updating = false;
        }
    }
}
