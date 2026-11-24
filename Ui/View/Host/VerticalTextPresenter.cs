using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace _1RM.View.Host
{
    /// <summary>
    /// Renders vertical tab labels in mixed orientation: CJK/fullwidth characters stand upright
    /// stacked top-to-bottom, while contiguous ASCII/Latin/digit/IP runs rotate 90° clockwise
    /// along the rail, with pixel-accurate ellipsis truncation when vertical space is constrained.
    /// </summary>
    public class VerticalTextPresenter : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(VerticalTextPresenter),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            nameof(Foreground), typeof(Brush), typeof(VerticalTextPresenter),
            new FrameworkPropertyMetadata(Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
            nameof(FontFamily), typeof(FontFamily), typeof(VerticalTextPresenter),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
            nameof(FontSize), typeof(double), typeof(VerticalTextPresenter),
            new FrameworkPropertyMetadata(11.5,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        private readonly struct TextSegment
        {
            public readonly string Content;
            public readonly bool IsUpright;

            public TextSegment(string content, bool isUpright)
            {
                Content = content;
                IsUpright = isUpright;
            }
        }

        private static bool IsUprightVerticalChar(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                   || (c >= 0xF900 && c <= 0xFAFF)
                   || (c >= 0xAC00 && c <= 0xD7AF)
                   || (c >= 0xFF00 && c <= 0xFFEF);
        }

        private static List<TextSegment> SegmentText(string text)
        {
            var segments = new List<TextSegment>();
            var i = 0;
            while (i < text.Length)
            {
                if (IsUprightVerticalChar(text[i]))
                {
                    segments.Add(new TextSegment(text[i].ToString(), isUpright: true));
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < text.Length && !IsUprightVerticalChar(text[i]))
                        i++;
                    segments.Add(new TextSegment(text.Substring(start, i - start), isUpright: false));
                }
            }
            return segments;
        }

        private FormattedText CreateFormattedText(string text, Typeface typeface, Brush foreground, double pixelsPerDip)
        {
            return new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                Math.Max(6.0, FontSize),
                foreground,
                pixelsPerDip);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var text = Text;
            if (string.IsNullOrEmpty(text))
                return new Size(0, 0);

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var foreground = Foreground ?? Brushes.White;
            var uprightStepY = Math.Ceiling(Math.Max(6.0, FontSize) * 1.12);

            var segments = SegmentText(text);
            var totalH = 0.0;
            var maxW = 0.0;

            foreach (var seg in segments)
            {
                var ft = CreateFormattedText(seg.Content, typeface, foreground, dpi);
                if (seg.IsUpright)
                {
                    totalH += uprightStepY;
                    if (ft.Width > maxW)
                        maxW = ft.Width;
                }
                else
                {
                    totalH += ft.WidthIncludingTrailingWhitespace;
                    if (ft.Height > maxW)
                        maxW = ft.Height;
                }
            }

            var w = double.IsInfinity(availableSize.Width) ? Math.Ceiling(maxW) : Math.Min(Math.Ceiling(maxW), availableSize.Width);
            var h = double.IsInfinity(availableSize.Height) ? Math.Ceiling(totalH) : Math.Min(Math.Ceiling(totalH), availableSize.Height);
            return new Size(w, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (RenderSize.Width < 6 || RenderSize.Height < 6 || string.IsNullOrEmpty(Text))
                return;

            dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var typeface = new Typeface(FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var foreground = Foreground ?? Brushes.White;
                var uprightStepY = Math.Ceiling(Math.Max(6.0, FontSize) * 1.12);

                var segments = SegmentText(Text);
                var formatted = new FormattedText[segments.Count];
                var totalH = 0.0;

                for (var i = 0; i < segments.Count; i++)
                {
                    var ft = CreateFormattedText(segments[i].Content, typeface, foreground, dpi);
                    formatted[i] = ft;
                    totalH += segments[i].IsUpright ? uprightStepY : ft.WidthIncludingTrailingWhitespace;
                }

                var y = 0.0;
                if (totalH <= RenderSize.Height + 0.5)
                {
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        var ft = formatted[i];
                        if (seg.IsUpright)
                        {
                            var x = (RenderSize.Width - ft.Width) / 2.0;
                            var drawY = y + (uprightStepY - ft.Height) / 2.0;
                            dc.DrawText(ft, new Point(x, drawY));
                            y += uprightStepY;
                        }
                        else
                        {
                            DrawRotated90(dc, ft, RenderSize.Width, y);
                            y += ft.WidthIncludingTrailingWhitespace;
                        }
                    }
                    return;
                }

                // Overflowing: fit as much as possible and end cleanly with a rotated ellipsis
                var ftEllipsis = CreateFormattedText("…", typeface, foreground, dpi);
                var ellipsisH = Math.Max(4.0, ftEllipsis.Width);
                var limitY = Math.Max(0.0, RenderSize.Height - ellipsisH);

                for (var i = 0; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    var ft = formatted[i];
                    if (seg.IsUpright)
                    {
                        if (y + uprightStepY <= limitY)
                        {
                            var x = (RenderSize.Width - ft.Width) / 2.0;
                            var drawY = y + (uprightStepY - ft.Height) / 2.0;
                            dc.DrawText(ft, new Point(x, drawY));
                            y += uprightStepY;
                        }
                        else
                        {
                            DrawRotated90(dc, ftEllipsis, RenderSize.Width, y);
                            break;
                        }
                    }
                    else
                    {
                        var segH = ft.WidthIncludingTrailingWhitespace;
                        if (y + segH <= limitY)
                        {
                            DrawRotated90(dc, ft, RenderSize.Width, y);
                            y += segH;
                        }
                        else
                        {
                            var rem = RenderSize.Height - y;
                            if (rem >= ellipsisH + 4.0)
                            {
                                ft.MaxTextWidth = rem;
                                ft.MaxLineCount = 1;
                                ft.Trimming = TextTrimming.CharacterEllipsis;
                                DrawRotated90(dc, ft, RenderSize.Width, y);
                            }
                            else
                            {
                                DrawRotated90(dc, ftEllipsis, RenderSize.Width, y);
                            }
                            break;
                        }
                    }
                }
            }
            finally
            {
                dc.Pop();
            }
        }

        private static void DrawRotated90(DrawingContext dc, FormattedText ft, double containerWidth, double y)
        {
            dc.PushTransform(new TranslateTransform((containerWidth + ft.Height) / 2.0, y));
            dc.PushTransform(new RotateTransform(90));
            dc.DrawText(ft, new Point(0, 0));
            dc.Pop();
            dc.Pop();
        }
    }
}
