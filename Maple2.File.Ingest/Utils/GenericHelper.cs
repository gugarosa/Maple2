using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Maple2.File.Ingest.Utils;

public static class GenericHelper {
    public static void SetValue(PropertyInfo property, object target, string value) {
        property.SetValue(target, ConvertValue(property.PropertyType, value));
    }

    private static object? ConvertValue(Type type, string value) {
        Type targetType = Nullable.GetUnderlyingType(type) ?? type;
        string input = targetType == typeof(string) ? value : value.Trim();

        if (Nullable.GetUnderlyingType(type) != null && input.Length == 0) {
            return null;
        }
        if (targetType == typeof(string)) {
            return input;
        }
        if (targetType == typeof(bool)) {
            return input switch {
                "1" => true,
                "0" => false,
                _ => bool.Parse(input),
            };
        }
        if (targetType.IsEnum) {
            return Enum.Parse(targetType, input, true);
        }
        if (targetType.IsArray) {
            Type elementType = targetType.GetElementType()
                ?? throw new InvalidOperationException($"Array type {targetType} has no element type.");
            if (input.Length == 0) {
                return Array.CreateInstance(elementType, 0);
            }

            string[] values = input.Split(',', StringSplitOptions.TrimEntries);
            Array result = Array.CreateInstance(elementType, values.Length);
            for (int i = 0; i < values.Length; i++) {
                result.SetValue(ConvertValue(elementType, values[i]), i);
            }
            return result;
        }
        if (targetType == typeof(Vector3)) {
            string[] values = input.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 3) {
                throw new FormatException($"Expected three vector components, got {values.Length}.");
            }

            return new Vector3(
                float.Parse(TrimNumericSuffix(values[0]), CultureInfo.InvariantCulture),
                float.Parse(TrimNumericSuffix(values[1]), CultureInfo.InvariantCulture),
                float.Parse(TrimNumericSuffix(values[2]), CultureInfo.InvariantCulture));
        }
        if (targetType == typeof(TimeSpan)) {
            return TimeSpan.Parse(input, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(DateTime)) {
            return DateTime.Parse(input, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind);
        }
        if (targetType == typeof(float) || targetType == typeof(double) || targetType == typeof(decimal)) {
            input = TrimNumericSuffix(input);
        }

        return Convert.ChangeType(input, targetType, CultureInfo.InvariantCulture);
    }

    private static string TrimNumericSuffix(string value) {
        return value.EndsWith('f') || value.EndsWith('F') ||
               value.EndsWith('d') || value.EndsWith('D') ||
               value.EndsWith('m') || value.EndsWith('M')
            ? value[..^1]
            : value;
    }
}
