using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static NoiseReduction.WinUI.MicrophoneText;

namespace NoiseReduction.WinUI
{
    // Три пресета силы, точное значение в дБ остаётся за ними; у каждой модели свой рабочий диапазон
    internal sealed class StrengthPresets
    {
        // Быстрая: доля исходного звука, 60 значит без подмешивания; Точная: предел самой модели, 60 снимает его и включает постфильтр
        internal static readonly int[] FastValues = { 6, 12, 60 };
        internal static readonly int[] PreciseValues = { 12, 24, 60 };
        internal static int[] ValuesFor(bool precise) => precise ? PreciseValues : FastValues;
        internal int[] Values { get; private set; } = PreciseValues;
        private static readonly string[] Names = { "Мягко", "Обычно", "Сильно" };
        private readonly ToggleButton[] buttons = new ToggleButton[3];
        private readonly ToggleButton custom;
        private bool updating;
        internal readonly Grid View = new Grid { ColumnSpacing = 4 };
        internal event Action<int> Selected;
        internal event Action CustomClicked;
        internal bool CustomOpen;

        // withCustom добавляет кнопку Своё для точной настройки, в панели трея её нет
        internal StrengthPresets(bool withCustom = false, bool compact = false)
        {
            for (int index = 0; index < buttons.Length; index++)
            {
                int position = index;
                var button = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
                // в панели трея кнопки мельче, чтобы не перевешивали название микрофона
                if (compact) { button.Padding = new Thickness(6, 1, 6, 2); button.MinHeight = 0; button.Height = 24; button.FontSize = 12; }
                button.Click += (_, _) => { if (!updating) Selected?.Invoke(Values[position]); };
                View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(button, index); View.Children.Add(button);
                buttons[index] = button;
            }
            if (!withCustom) return;
            custom = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
            custom.Click += (_, _) => { if (!updating) CustomClicked?.Invoke(); };
            View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(custom, buttons.Length); View.Children.Add(custom);
        }

        internal void Localize()
        {
            for (int index = 0; index < buttons.Length; index++) { buttons[index].Content = T(Names[index]); ToolTipService.SetToolTip(buttons[index], Values[index] >= 60 ? T("Максимум") : Values[index] + " " + T("дБ")); }
            if (custom != null) custom.Content = T("Своё");
        }

        internal void Update(int value, bool enabled, bool precise)
        {
            if (Values != ValuesFor(precise)) { Values = ValuesFor(precise); Localize(); }
            updating = true;
            bool preset = Array.IndexOf(Values, value) >= 0;
            for (int index = 0; index < buttons.Length; index++) { buttons[index].IsChecked = Values[index] == value && !CustomOpen; buttons[index].IsEnabled = enabled; }
            if (custom != null) { custom.IsChecked = CustomOpen || !preset; custom.IsEnabled = enabled; }
            updating = false;
        }
    }
}
