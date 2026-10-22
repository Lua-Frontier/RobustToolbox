using System.Collections.Frozen;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Definition;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Shared.Serialization;

public sealed partial class BoxedWriterTest
{
    private delegate DataNode WriteGenericDelegate<T>(T value, bool alwaysWrite, ISerializationContext? context);
    private WriteGenericDelegate<T> LegacyStrategy<T>(T value, bool notNullable)
    {
        static object ValueFactory(Type baseType, Type actualType, bool notNullableOverride, SerializationManager serializationManager)
        {
            var instanceParam = Expression.Constant(serializationManager);
            var objParam = Expression.Parameter(baseType, "value");
            var alwaysWriteParam = Expression.Parameter(typeof(bool), "alwaysWrite");
            var contextParam = Expression.Parameter(typeof(ISerializationContext), "context");

            actualType = actualType.EnsureNotNullableType();
            var sameType = baseType.EnsureNotNullableType() == actualType;

            Expression objAccess = baseType.IsNullable()
                ? Expression.Convert(objParam, sameType ? baseType.EnsureNotNullableType() : actualType)
                : sameType ? objParam : Expression.Convert(objParam, actualType);

            if (baseType.IsGenericType)
            {
                var t = baseType.GetGenericTypeDefinition();
                if (t == typeof(FrozenDictionary<,>) || t == typeof(FrozenSet<>))
                    actualType = baseType;
            }

            Expression call;
            if (((SerializationManager.SerializerProvider)typeof(SerializationManager).GetField("_regularSerializerProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serializationManager)!).TryGetTypeSerializer(typeof(ITypeWriter<>), actualType, out var serializer))
            {
                var serializerConst = Expression.Constant(serializer);
                call = Expression.Call(
                    instanceParam,
                    nameof(SerializationManager.WriteValue),
                    new []{actualType},
                    serializerConst,
                    objAccess,
                    alwaysWriteParam,
                    contextParam,
                    Expression.Constant(notNullableOverride)
                );
            }
            else if (actualType.IsEnum)
            {
                if (baseType != typeof(Enum) ||
                    !((SerializationManager.SerializerProvider)typeof(SerializationManager).GetField("_regularSerializerProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serializationManager)!).TryGetTypeSerializer(typeof(ITypeWriter<>),
                        typeof(Enum), out serializer))
                {
                    call = Expression.Call(
                        instanceParam,
                        "WriteConvertible",
                        Type.EmptyTypes,
                        Expression.Convert(objParam, typeof(IConvertible)));
                }
                else
                {
                    var serializerConst = Expression.Constant(serializer);
                    call = Expression.Call(
                        instanceParam,
                        nameof(SerializationManager.WriteValue),
                        new []{typeof(Enum)},
                        serializerConst,
                        Expression.Convert(objParam, typeof(Enum)),
                        alwaysWriteParam,
                        contextParam,
                        Expression.Constant(notNullableOverride)
                    );
                }
            }
            else if (actualType.IsArray)
            {
                call = Expression.Call(
                    instanceParam,
                    "WriteArray",
                    new []{ actualType.GetElementType()! },
                    Expression.Convert(objParam, actualType),
                    alwaysWriteParam,
                    contextParam);
            }
            else if (typeof(ISelfSerialize).IsAssignableFrom(actualType))
            {
                call = Expression.Call(
                    instanceParam,
                    "WriteSelfSerializable",
                    Type.EmptyTypes,
                    Expression.Convert(objParam, typeof(ISelfSerialize)));
            }
            else if (actualType.IsAssignableTo(typeof(ISerializationGenerated<>).MakeGenericType(actualType)))
            {
                call = Expression.Call(
                    instanceParam,
                    "WriteValueInternal",
                    new[] { actualType },
                    objAccess,
                    Expression.Constant(serializationManager.GetDefinition(actualType), typeof(DataDefinition<>).MakeGenericType(actualType)),
                    alwaysWriteParam,
                    contextParam);

                if (!sameType)
                {
                    var nodeVar = Expression.Variable(typeof(DataNode));
                    call = Expression.Block(
                        new[] { nodeVar },
                        Expression.Assign(nodeVar, call),
                        Expression.Assign(Expression.Field(nodeVar, "Tag"), Expression.Constant($"!type:{actualType.Name}")),
                        nodeVar);
                }
            }
            else
            {
                call = Expression.Call(
                    instanceParam,
                    "WriteNoSerializer",
                    Type.EmptyTypes,
                    Expression.Constant(actualType, typeof(Type)));
            }

            var serializerType = typeof(ITypeWriter<>).MakeGenericType(actualType);
            var serializerVar = Expression.Variable(serializerType);
            call = Expression.Block(
                new[] { serializerVar },
                Expression.Condition(
                    Expression.AndAlso(
                        Expression.ReferenceNotEqual(contextParam,
                            Expression.Constant(null, typeof(ISerializationContext))),
                        Expression.Call(
                            Expression.Property(contextParam, "SerializerProvider"),
                            "TryGetTypeSerializer",
                            new[] { serializerType, actualType },
                            serializerVar)),
                    Expression.Call(
                        instanceParam,
                        nameof(SerializationManager.WriteValue),
                        new []{actualType},
                        serializerVar,
                        objAccess,
                        alwaysWriteParam,
                        contextParam,
                        Expression.Constant(notNullableOverride)),
                    call));

            return Expression.Lambda<WriteGenericDelegate<T>>(
                call,
                objParam,
                alwaysWriteParam,
                contextParam).Compile();
        }

        var manager = (SerializationManager)Serialization;
        return (WriteGenericDelegate<T>)ValueFactory(typeof(T), typeof(T).IsSealed ? typeof(T) : value!.GetType(), notNullable, manager);
    }

    private DataNode LegacyGeneric<T>(T value, bool alwaysWrite, ISerializationContext? context, bool notNullable)
    {
        var node = LegacyStrategy(value, notNullable)(value, alwaysWrite, context);
        if (typeof(T) == typeof(object)) node.Tag = "!type:" + value!.GetType().Name;
        return node;
    }

    private static IEnumerable<TestCaseData> GenericValues()
    {
        foreach (var row in Values()) yield return row;
        yield return new TestCaseData(typeof(Enum), DayOfWeek.Friday);
        yield return new TestCaseData(typeof(Array), new[] { 3, 6 });
        yield return new TestCaseData(typeof(ISelfSerialize), new SelfValue());
        yield return new TestCaseData(typeof(FrozenDictionary<string, int>), new Dictionary<string, int> { ["one"] = 1 }.ToFrozenDictionary());
        yield return new TestCaseData(typeof(FrozenSet<int>), new[] { 1, 7 }.ToFrozenSet());
    }

    [TestCaseSource(nameof(GenericValues))]
    public void TypedStrategyMatchesLegacyExpressions(Type type, object value)
    {
        var legacy = GetType().GetMethod(nameof(LegacyGeneric), BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(type);
        foreach (var always in new[] { false, true })
        foreach (var notNullable in new[] { false, true })
        foreach (var context in new ISerializationContext?[] { null, new IntContext(Serialization) })
        {
            var expected = (DataNode)legacy.Invoke(this, [value, always, context, notNullable])!;
            var actual = Serialization.WriteValue(type, value, always, context, notNullable);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual.Tag, Is.EqualTo(expected.Tag));
        }
    }

    [Test]
    public void HotTypedWriterDoesNotAddBoxingAllocations()
    {
        var old = LegacyStrategy(12, false);
        Serialization.WriteValue(12);
        var factory = typeof(SerializationManager).GetMethod("GetOrCreateWriteGenericDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
        var generated = (Delegate)factory.Invoke(Serialization, new object[] { 12, false })!;
        var direct = (WriteGenericDelegate<int>)Delegate.CreateDelegate(typeof(WriteGenericDelegate<int>), generated.Target, generated.Method);
        for (var i = 0; i < 1000; i++) { old(12, false, null); direct(12, false, null); Serialization.WriteValue(12); }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) old(12, false, null);
        var baseline = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) direct(12, false, null);
        var current = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.That(current, Is.LessThanOrEqualTo(baseline));
        TestContext.Out.WriteLine($"10000 warmed integer writes: old-strategy={baseline}B, direct-call={current}B");
    }

    private sealed class SelfValue : ISelfSerialize
    {
        public string Serialize() => "self:world";
        public void Deserialize(string value) { }
    }
}
