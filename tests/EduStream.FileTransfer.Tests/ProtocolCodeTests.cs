using System.Reflection;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

public sealed class ProtocolCodeTests
{
    [Fact]
    public void ErrorCodes_ShouldBeUniqueAndNonEmpty()
    {
        var values = GetPublicStringConstants(typeof(ErrorCodes));

        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AckCodes_ShouldBeUniqueAndNonEmpty()
    {
        var values = GetPublicStringConstants(typeof(AckCodes));

        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    private static IReadOnlyList<string> GetPublicStringConstants(Type type)
    {
        return type
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
    }
}
