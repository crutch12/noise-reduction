using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NoiseReduction.WinUI
{
    // Тонкий акрил прозрачнее стандартного и сильнее показывает размытый фон
    internal sealed class ThinAcrylic : SystemBackdrop
    {
        private readonly Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController> controllers = new Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController>();

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
            controller.AddSystemBackdropTarget(target);
            controllers[target] = controller;
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            base.OnTargetDisconnected(target);
            if (!controllers.Remove(target, out var controller)) return;
            controller.RemoveSystemBackdropTarget(target);
            controller.Dispose();
        }
    }
}
