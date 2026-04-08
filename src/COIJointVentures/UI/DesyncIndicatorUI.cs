using UnityEngine;
using UnityEngine.UIElements;

namespace COIJointVentures.UI;

internal sealed class DesyncIndicatorUI
{
    private readonly VisualElement _root;
    private readonly Label _warningLabel;

    public VisualElement Root => _root;

    public bool IsDesynced
    {
        get => _root.style.display == DisplayStyle.Flex;
        set => _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public DesyncIndicatorUI()
    {
        _root = new VisualElement();
        _root.style.position = Position.Absolute;
        _root.style.right = 16;
        _root.style.top = 16;
        _root.style.backgroundColor = new Color(0.8f, 0.1f, 0.1f, 0.9f);
        _root.style.paddingLeft = 12;
        _root.style.paddingRight = 12;
        _root.style.paddingTop = 8;
        _root.style.paddingBottom = 8;
        _root.style.display = DisplayStyle.None;

        UIHelpers.SetBorderRadius(_root, 4);
        UIHelpers.SetBorder(_root, 2, Color.white);

        _warningLabel = new Label("⚠️ DESYNC DETECTED");
        _warningLabel.style.fontSize = 16;
        _warningLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        _warningLabel.style.color = Color.white;
        _root.Add(_warningLabel);
    }
}
