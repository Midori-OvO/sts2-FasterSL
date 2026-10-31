using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace FasterSL;

/// <summary>节点列表和回合列表共用的控件样式。字体按当前语言做和游戏相同的替换，保证中日韩泰俄等文字能显示。</summary>
internal static class Ui
{
	public static StyleBoxFlat Box(Color color, float corner = 6f, float marginX = 16f, float marginY = 8f)
	{
		int radius = (int)corner;
		return new StyleBoxFlat
		{
			BgColor = color,
			CornerRadiusTopLeft = radius,
			CornerRadiusTopRight = radius,
			CornerRadiusBottomLeft = radius,
			CornerRadiusBottomRight = radius,
			ContentMarginLeft = marginX,
			ContentMarginRight = marginX,
			ContentMarginTop = marginY,
			ContentMarginBottom = marginY
		};
	}

	public static StyleBoxFlat Panel(float marginX, float marginY)
	{
		StyleBoxFlat box = Box(new Color(0.09f, 0.1f, 0.13f, 0.97f), 10f, marginX, marginY);
		box.BorderColor = new Color(0.78f, 0.64f, 0.38f);
		box.BorderWidthLeft = 2;
		box.BorderWidthTop = 2;
		box.BorderWidthRight = 2;
		box.BorderWidthBottom = 2;
		return box;
	}

	public static Label MakeLabel(string text, int size, Color color)
	{
		Label label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		label.AddThemeFontSizeOverride("font_size", size);
		label.AddThemeColorOverride("font_color", color);
		label.ApplyLocaleFontSubstitution(FontType.Regular, "font");
		return label;
	}

	public static Button MakeButton(string text, int size)
	{
		Button button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.AddThemeFontSizeOverride("font_size", size);
		button.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
		button.AddThemeColorOverride("font_hover_color", new Color(1f, 0.92f, 0.7f));
		button.AddThemeStyleboxOverride("normal", Box(new Color(0.16f, 0.17f, 0.21f)));
		button.AddThemeStyleboxOverride("hover", Box(new Color(0.27f, 0.24f, 0.18f)));
		button.AddThemeStyleboxOverride("pressed", Box(new Color(0.36f, 0.3f, 0.18f)));
		button.ApplyLocaleFontSubstitution(FontType.Regular, "font");
		return button;
	}
}
