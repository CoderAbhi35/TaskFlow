namespace Reeve.Application.Common;

/// <summary>
/// Maps between domain and contract enums by member name. A unit test asserts every member maps
/// in both directions, so adding a status on one side without the other fails the build's tests.
/// </summary>
public static class EnumMapper
{
    public static TTo Map<TFrom, TTo>(TFrom value)
        where TFrom : struct, Enum
        where TTo : struct, Enum =>
        Enum.Parse<TTo>(value.ToString());
}
