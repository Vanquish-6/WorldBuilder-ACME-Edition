using Avalonia.Data.Converters;
using System;
using System.Globalization;
using WorldBuilder.Lib.Docking;

namespace WorldBuilder.Lib.Converters {
    public class OrientationToColumnsConverter : IValueConverter {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
            if (value is Orientation orientation) {
                return orientation == Orientation.Vertical ? 1 : 0;
            }
            return 0; // 0 means auto in UniformGrid Columns? No, 0 means auto usually.
            // UniformGrid: If Rows=0, Columns=Fixed -> fills columns then rows.
            // If Columns=0, Rows=Fixed -> fills rows then columns.
            // Vertical Stack: 1 Column, N Rows. So Columns=1.
            // Horizontal Stack: N Columns, 1 Row. So Rows=1.
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }

    public class OrientationToRowsConverter : IValueConverter {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
            if (value is Orientation orientation) {
                return orientation == Orientation.Horizontal ? 1 : 0;
            }
            return 0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
