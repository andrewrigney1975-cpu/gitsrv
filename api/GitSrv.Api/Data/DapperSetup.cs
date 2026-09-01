using System.Data;
using System.Reflection;
using Dapper;

namespace GitSrv.Api.Data;

/// <summary>
/// Dapper maps <c>snake_case</c> columns to PascalCase <em>properties</em> when
/// <see cref="DefaultTypeMap.MatchNamesWithUnderscores"/> is set, but it does <b>not</b> do the same
/// for <em>constructor parameters</em> — and positional records only have a constructor. This wraps
/// the default map so constructor-parameter matching also ignores underscores, letting
/// <c>SELECT display_name</c> bind to a <c>DisplayName</c> record parameter without column aliases.
/// </summary>
public sealed class UnderscoreConstructorTypeMap(Type type) : SqlMapper.ITypeMap
{
    private readonly SqlMapper.ITypeMap _inner = new DefaultTypeMap(type);

    public ConstructorInfo? FindConstructor(string[] names, Type[] types) => _inner.FindConstructor(names, types);
    public ConstructorInfo? FindExplicitConstructor() => _inner.FindExplicitConstructor();
    public SqlMapper.IMemberMap? GetMember(string columnName) => _inner.GetMember(columnName);

    public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
    {
        var map = _inner.GetConstructorParameter(constructor, columnName);
        if (map is not null || !columnName.Contains('_'))
            return map;

        var collapsed = columnName.Replace("_", "");
        var param = constructor.GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, collapsed, StringComparison.OrdinalIgnoreCase));
        return param is null ? null : new SimpleParameterMap(columnName, param);
    }

    private sealed class SimpleParameterMap(string columnName, ParameterInfo parameter) : SqlMapper.IMemberMap
    {
        public string ColumnName => columnName;
        public Type MemberType => parameter.ParameterType;
        public ParameterInfo Parameter => parameter;
        public PropertyInfo? Property => null;
        public FieldInfo? Field => null;
    }

    public static void Register(params Type[] types)
    {
        foreach (var t in types)
            SqlMapper.SetTypeMap(t, new UnderscoreConstructorTypeMap(t));
    }
}
