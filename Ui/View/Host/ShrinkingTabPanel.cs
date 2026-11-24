using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace _1RM.View.Host
{
    /// <summary>
    /// Chrome-style adaptive tab panel that shrinks tabs proportionally (water-filling) to keep
    /// all open sessions visible inside the available viewport instead of hiding them off-screen.
    /// Falls back to MinTabLength (allowing ScrollViewer scrolling) only when even minimum-width
    /// tabs exceed the viewport.
    /// </summary>
    public class ShrinkingTabPanel : Panel
    {
        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
            nameof(Orientation), typeof(Orientation), typeof(ShrinkingTabPanel),
            new FrameworkPropertyMetadata(Orientation.Horizontal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public static readonly DependencyProperty ViewportLengthProperty = DependencyProperty.Register(
            nameof(ViewportLength), typeof(double), typeof(ShrinkingTabPanel),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double ViewportLength
        {
            get => (double)GetValue(ViewportLengthProperty);
            set => SetValue(ViewportLengthProperty, value);
        }

        public static readonly DependencyProperty MinTabLengthProperty = DependencyProperty.Register(
            nameof(MinTabLength), typeof(double), typeof(ShrinkingTabPanel),
            new FrameworkPropertyMetadata(38.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double MinTabLength
        {
            get => (double)GetValue(MinTabLengthProperty);
            set => SetValue(MinTabLengthProperty, value);
        }

        public static readonly DependencyProperty MaxTabLengthProperty = DependencyProperty.Register(
            nameof(MaxTabLength), typeof(double), typeof(ShrinkingTabPanel),
            new FrameworkPropertyMetadata(180.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double MaxTabLength
        {
            get => (double)GetValue(MaxTabLengthProperty);
            set => SetValue(MaxTabLengthProperty, value);
        }

        public static readonly DependencyProperty CompressThresholdProperty = DependencyProperty.Register(
            nameof(CompressThreshold), typeof(double), typeof(ShrinkingTabPanel),
            new FrameworkPropertyMetadata(68.0, FrameworkPropertyMetadataOptions.AffectsArrange));

        public double CompressThreshold
        {
            get => (double)GetValue(CompressThresholdProperty);
            set => SetValue(CompressThresholdProperty, value);
        }

        public static readonly DependencyProperty IsCompressedProperty = DependencyProperty.RegisterAttached(
            "IsCompressed", typeof(bool), typeof(ShrinkingTabPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsCompressed(DependencyObject obj) => (bool)obj.GetValue(IsCompressedProperty);
        public static void SetIsCompressed(DependencyObject obj, bool value) => obj.SetValue(IsCompressedProperty, value);

        private readonly List<double> _allocatedMainLengths = new();

        protected override Size MeasureOverride(Size availableSize)
        {
            var children = InternalChildren;
            var count = children.Count;
            _allocatedMainLengths.Clear();
            if (count == 0)
                return new Size(0, 0);

            var isHorizontal = Orientation == Orientation.Horizontal;
            var crossAvail = isHorizontal ? availableSize.Height : availableSize.Width;
            var availMain = isHorizontal ? availableSize.Width : availableSize.Height;
            var minLen = Math.Max(16.0, MinTabLength);
            var maxLen = Math.Max(minLen, MaxTabLength);

            var pass1Constraint = isHorizontal
                ? new Size(maxLen, crossAvail)
                : new Size(crossAvail, maxLen);

            var natural = new double[count];
            var isVisible = new bool[count];
            var visibleCount = 0;
            var sumNatural = 0.0;
            var maxCross = 0.0;

            for (var i = 0; i < count; i++)
            {
                var child = children[i];
                if (child.Visibility == Visibility.Collapsed)
                {
                    _allocatedMainLengths.Add(0);
                    continue;
                }

                isVisible[i] = true;
                visibleCount++;
                child.Measure(pass1Constraint);
                var desiredMain = isHorizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
                var desiredCross = isHorizontal ? child.DesiredSize.Height : child.DesiredSize.Width;
                var clampedMain = Math.Clamp(desiredMain, minLen, maxLen);
                natural[i] = clampedMain;
                sumNatural += clampedMain;
                if (desiredCross > maxCross)
                    maxCross = desiredCross;
                _allocatedMainLengths.Add(clampedMain);
            }

            if (visibleCount == 0)
                return new Size(0, 0);

            double viewport;
            if (!double.IsNaN(ViewportLength) && !double.IsInfinity(ViewportLength) && ViewportLength > 1.0)
                viewport = ViewportLength;
            else if (!double.IsNaN(availMain) && !double.IsInfinity(availMain) && availMain > 1.0)
                viewport = availMain;
            else
                viewport = double.PositiveInfinity;

            var alloc = new double[count];
            if (double.IsInfinity(viewport) || sumNatural <= viewport)
            {
                Array.Copy(natural, alloc, count);
            }
            else if (visibleCount * minLen >= viewport)
            {
                for (var i = 0; i < count; i++)
                {
                    if (isVisible[i])
                        alloc[i] = minLen;
                }
            }
            else
            {
                // Water-filling: keep short tabs at natural size and shrink longer tabs evenly
                var locked = new bool[count];
                var remainingBudget = viewport;
                var remainingCount = visibleCount;

                while (remainingCount > 0)
                {
                    var share = remainingBudget / remainingCount;
                    var lockedAny = false;
                    for (var i = 0; i < count; i++)
                    {
                        if (!isVisible[i] || locked[i])
                            continue;
                        if (natural[i] <= share)
                        {
                            locked[i] = true;
                            alloc[i] = natural[i];
                            remainingBudget -= natural[i];
                            remainingCount--;
                            lockedAny = true;
                        }
                    }

                    if (!lockedAny)
                        break;
                }

                if (remainingCount > 0)
                {
                    var sharedLen = Math.Floor(Math.Max(minLen, remainingBudget / remainingCount));
                    for (var i = 0; i < count; i++)
                    {
                        if (isVisible[i] && !locked[i])
                            alloc[i] = sharedLen;
                    }
                }

                // Prevent sub-pixel rounding from triggering an unnecessary 1px scrollbar
                var sumAlloc = 0.0;
                for (var i = 0; i < count; i++)
                {
                    if (isVisible[i])
                        sumAlloc += alloc[i];
                }
                if (sumAlloc > viewport)
                {
                    var excess = sumAlloc - Math.Floor(viewport);
                    for (var i = count - 1; i >= 0 && excess > 0.01; i--)
                    {
                        if (!isVisible[i] || alloc[i] <= minLen)
                            continue;
                        var trim = Math.Min(excess, alloc[i] - minLen);
                        alloc[i] -= trim;
                        excess -= trim;
                    }
                }
            }

            var totalMain = 0.0;
            for (var i = 0; i < count; i++)
            {
                if (!isVisible[i])
                    continue;
                _allocatedMainLengths[i] = alloc[i];
                totalMain += alloc[i];

                if (alloc[i] < natural[i] - 0.5)
                {
                    var pass2Constraint = isHorizontal
                        ? new Size(alloc[i], crossAvail)
                        : new Size(crossAvail, alloc[i]);
                    children[i].Measure(pass2Constraint);
                }
            }

            var finalCross = double.IsInfinity(crossAvail) ? maxCross : Math.Min(maxCross, crossAvail);
            return isHorizontal
                ? new Size(totalMain, finalCross)
                : new Size(finalCross, totalMain);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var children = InternalChildren;
            var isHorizontal = Orientation == Orientation.Horizontal;
            var threshold = CompressThreshold;
            var offset = 0.0;

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child.Visibility == Visibility.Collapsed)
                {
                    SetIsCompressed(child, false);
                    continue;
                }

                var len = i < _allocatedMainLengths.Count
                    ? _allocatedMainLengths[i]
                    : (isHorizontal ? child.DesiredSize.Width : child.DesiredSize.Height);

                SetIsCompressed(child, len < threshold);
                var rect = isHorizontal
                    ? new Rect(offset, 0, len, finalSize.Height)
                    : new Rect(0, offset, finalSize.Width, len);
                child.Arrange(rect);
                offset += len;
            }

            return finalSize;
        }
    }
}
