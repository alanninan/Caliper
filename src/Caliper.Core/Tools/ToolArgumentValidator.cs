// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;

namespace Caliper.Core.Tools;

internal static class ToolArgumentValidator
{
    internal static string? Validate(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return $"$ must be object, got {KindName(arguments.ValueKind)}";

        if (schema.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrEmpty(name) && !arguments.TryGetProperty(name, out _))
                    return $"$.{name} is required";
            }
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                known.Add(property.Name);
                if (arguments.TryGetProperty(property.Name, out var value))
                {
                    var error = ValidateProperty(property.Name, value, property.Value);
                    if (error is not null)
                        return error;
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind == JsonValueKind.False)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                if (!known.Contains(property.Name))
                    return $"$.{property.Name} is not allowed";
            }
        }

        return null;
    }

    private static string? ValidateProperty(string name, JsonElement value, JsonElement propertySchema)
    {
        if (propertySchema.TryGetProperty("type", out var type))
        {
            var error = ValidateType(name, value, type);
            if (error is not null)
                return error;
        }

        if (propertySchema.TryGetProperty("enum", out var enumValues) &&
            enumValues.ValueKind == JsonValueKind.Array)
        {
            var allowed = enumValues.EnumerateArray().Any(enumValue => JsonElementEquals(value, enumValue));
            if (!allowed)
                return $"$.{name} must be one of [{string.Join(", ", enumValues.EnumerateArray().Select(v => v.GetRawText()))}]";
        }

        if (value.ValueKind == JsonValueKind.String &&
            propertySchema.TryGetProperty("maxLength", out var maxLength) &&
            maxLength.ValueKind == JsonValueKind.Number &&
            maxLength.TryGetInt32(out var max) &&
            value.GetString() is { } text &&
            text.Length > max)
        {
            return $"$.{name} must be at most {max} characters";
        }

        return null;
    }

    private static string? ValidateType(string name, JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String)
            return MatchesType(value, type.GetString()) ? null : TypeError(name, type.GetString(), value.ValueKind);

        if (type.ValueKind == JsonValueKind.Array)
        {
            var allowed = type.EnumerateArray()
                .Select(t => t.GetString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (allowed.Any(t => MatchesType(value, t)))
                return null;

            return $"$.{name} must be {string.Join(" or ", allowed)}, got {KindName(value.ValueKind)}";
        }

        return null;
    }

    private static bool MatchesType(JsonElement value, string? type) =>
        type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _))
            return true;

        return value.TryGetDecimal(out var dec) && decimal.Truncate(dec) == dec;
    }

    private static string TypeError(string name, string? expected, JsonValueKind actual) =>
        $"$.{name} must be {expected}, got {KindName(actual)}";

    private static string KindName(JsonValueKind kind) =>
        kind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant(),
        };

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        return left.ValueKind switch
        {
            JsonValueKind.Number => NumbersEqual(left, right),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
            return leftDecimal == rightDecimal;

        return left.TryGetDouble(out var leftDouble)
            && right.TryGetDouble(out var rightDouble)
            && leftDouble.Equals(rightDouble);
    }
}
