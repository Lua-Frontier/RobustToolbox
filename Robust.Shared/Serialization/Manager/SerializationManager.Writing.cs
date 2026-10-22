using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Robust.Shared.Serialization.Manager.Definition;
using Robust.Shared.Serialization.Manager.Exceptions;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Utility;

namespace Robust.Shared.Serialization.Manager;

public sealed partial class SerializationManager
{
    #region DelegateElements

    private delegate DataNode WriteBoxingDelegate(
        object value,
        bool alwaysWrite,
        ISerializationContext? context);

    private delegate DataNode WriteGenericDelegate<T>(
        T value,
        bool alwaysWrite,
        ISerializationContext? context);

    private readonly ConcurrentDictionary<(Type, bool), WriteBoxingDelegate> _writeBoxingDelegates = new();
    private readonly ConcurrentDictionary<(Type baseType, Type actualType, bool), object> _writeGenericBaseDelegates = new();
    private readonly ConcurrentDictionary<(Type, bool), object> _writeGenericDelegates = new();

    private static readonly MethodInfo BoxedWriterFactory = typeof(SerializationManager)
        .GetMethod(nameof(CreateBoxedWriter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private WriteBoxingDelegate CreateBoxedWriter<T>(bool notNullableOverride)
        => (value, alwaysWrite, context) => WriteValue((T)value, alwaysWrite, context, notNullableOverride);

    private WriteBoxingDelegate GetOrCreateWriteBoxingDelegate(Type type, bool notNullableOverride)
    {
        return _writeBoxingDelegates.GetOrAdd((type, notNullableOverride), static (tuple, manager) =>
        {
            return (WriteBoxingDelegate)BoxedWriterFactory.MakeGenericMethod(tuple.Item1)
                .Invoke(manager, new object[] { tuple.Item2 })!;
        }, this);
    }

    private WriteGenericDelegate<T> GetOrCreateWriteGenericDelegate<T>(T value, bool notNullableOverride)
    {
        static object ValueFactory(Type baseType, Type actualType, bool notNullableOverride, SerializationManager manager)
        {
            actualType = actualType.EnsureNotNullableType();
            var sameType = baseType.EnsureNotNullableType() == actualType;
            if (baseType.IsGenericType && (baseType.GetGenericTypeDefinition() == typeof(FrozenDictionary<,>)
                || baseType.GetGenericTypeDefinition() == typeof(FrozenSet<>)))
                actualType = baseType;

            return TypedWriterFactory.MakeGenericMethod(baseType, actualType)
                .Invoke(manager, new object[] { notNullableOverride, sameType })!;
        }

        var type = typeof(T);
        if (!type.IsSealed)
        {
            return (WriteGenericDelegate<T>)_writeGenericBaseDelegates.GetOrAdd((type, value!.GetType(), notNullableOverride),
                static (tuple, manager) => ValueFactory(tuple.baseType, tuple.actualType, tuple.Item3, manager), this);
        }

        return (WriteGenericDelegate<T>) _writeGenericDelegates
            .GetOrAdd((type, notNullableOverride), static (tuple, manager) => ValueFactory(tuple.Item1, tuple.Item1, tuple.Item2, manager), this);
    }

    private static readonly MethodInfo TypedWriterFactory = typeof(SerializationManager)
        .GetMethod(nameof(CreateTypedWriter), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ArrayWriterFactory = typeof(SerializationManager)
        .GetMethod(nameof(CreateArrayWriter), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GeneratedWriterFactory = typeof(SerializationManager)
        .GetMethod(nameof(CreateGeneratedWriter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private WriteGenericDelegate<TBase> CreateTypedWriter<TBase, TActual>(bool notNullableOverride, bool sameType)
    {
        WriteGenericDelegate<TActual> fallback;
        if (_regularSerializerProvider.TryGetTypeSerializer(typeof(ITypeWriter<>), typeof(TActual), out var serializer))
        {
            var writer = (ITypeWriter<TActual>)serializer;
            fallback = (value, alwaysWrite, context) => WriteValue(writer, value, alwaysWrite, context, notNullableOverride);
        }
        else if (typeof(TActual).IsEnum)
        {
            if (typeof(TBase) == typeof(Enum) && _regularSerializerProvider.TryGetTypeSerializer(
                    typeof(ITypeWriter<>), typeof(Enum), out serializer))
            {
                var writer = (ITypeWriter<Enum>)serializer;
                fallback = (value, alwaysWrite, context) => WriteValue(writer, (Enum)(object)value!, alwaysWrite, context, notNullableOverride);
            }
            else fallback = (value, _, _) => WriteConvertible((IConvertible)value!);
        }
        else if (typeof(TActual).IsArray)
            fallback = (WriteGenericDelegate<TActual>)ArrayWriterFactory.MakeGenericMethod(typeof(TActual).GetElementType()!)
                .Invoke(this, null)!;
        else if (typeof(ISelfSerialize).IsAssignableFrom(typeof(TActual)))
            fallback = (value, _, _) => WriteSelfSerializable((ISelfSerialize)value!);
        else if (typeof(TActual).IsAssignableTo(typeof(ISerializationGenerated<>).MakeGenericType(typeof(TActual))))
            fallback = (WriteGenericDelegate<TActual>)GeneratedWriterFactory.MakeGenericMethod(typeof(TActual))
                .Invoke(this, new object[] { sameType })!;
        else fallback = (_, _, _) => WriteNoSerializer(typeof(TActual));

        return (value, alwaysWrite, context) =>
        {
            var actual = typeof(TBase) == typeof(TActual)
                ? Unsafe.As<TBase, TActual>(ref value)
                : (TActual)(object)value!;
            if (context != null && context.SerializerProvider.TryGetTypeSerializer<ITypeWriter<TActual>, TActual>(out var custom))
                return WriteValue(custom, actual, alwaysWrite, context, notNullableOverride);
            return fallback(actual, alwaysWrite, context);
        };
    }

    private WriteGenericDelegate<TElement[]> CreateArrayWriter<TElement>()
        => WriteArray;

    private WriteGenericDelegate<T> CreateGeneratedWriter<T>(bool sameType) where T : ISerializationGenerated<T>
    {
        var definition = GetDefinition(typeof(T)) as DataDefinition<T>;
        return (value, alwaysWrite, context) =>
        {
            var node = WriteValueInternal(value, definition, alwaysWrite, context);
            if (!sameType) node.Tag = $"!type:{typeof(T).Name}";
            return node;
        };
    }

    private DataNode WriteConvertible(IConvertible obj)
    {
        return new ValueDataNode(obj.ToString(CultureInfo.InvariantCulture));
    }

    private DataNode WriteSelfSerializable(ISelfSerialize obj)
    {
        return new ValueDataNode(obj.Serialize());
    }

    private DataNode WriteNoSerializer(Type type)
    {
        throw new ArgumentException($"No type serializer or data definition found for type {type} when writing");
    }

    private DataNode WriteArray<TElement>(TElement[] obj, bool alwaysWrite, ISerializationContext? context)
    {
        var sequenceNode = new SequenceDataNode();

        foreach (var val in obj)
        {
            var serializedVal = WriteValue(val, alwaysWrite, context);
            sequenceNode.Add(serializedVal);
        }

        return sequenceNode;
    }

    private DataNode WriteValueInternal<T>(
        T value,
        DataDefinition<T>? definition,
        bool alwaysWrite,
        ISerializationContext? context)
        where T : ISerializationGenerated<T>
    {
        //this check is in here on purpose. we cannot check this during expression tree generation due to the value maybe being handled by a custom typeserializer
        if(definition == null)
            throw new InvalidOperationException($"No data definition found for type {typeof(T)} when writing");

        var mapping = new MappingDataNode();
        definition.Serialize(value, mapping, this, context, alwaysWrite, definition.DefaultValuesDict);

        return mapping;
    }

    #endregion

    public DataNode WriteValue<T>(T value, bool alwaysWrite = false, ISerializationContext? context = null, bool notNullableOverride = false)
    {
        if(value == null)
        {
            CanWriteNullCheck(typeof(T), notNullableOverride);
            return ValueDataNode.Null();
        }

        var node = GetOrCreateWriteGenericDelegate(value, notNullableOverride)(value, alwaysWrite, context);

        if (typeof(T) == typeof(object))
            node.Tag = "!type:" + value.GetType().Name;

        return node;
    }

    public DataNode WriteValue<T>(ITypeWriter<T> writer, T value, bool alwaysWrite = false,
        ISerializationContext? context = null, bool notNullableOverride = false)
    {
        if (value == null)
        {
            CanWriteNullCheck(typeof(T), notNullableOverride);
            return NullNode();
        }

        return writer.Write(this, value, DependencyCollection, alwaysWrite, context);
    }

    public DataNode WriteValue<T, TWriter>(T value, bool alwaysWrite = false, ISerializationContext? context = null, bool notNullableOverride = false)
        where TWriter : ITypeWriter<T>
    {
        return WriteValue(GetOrCreateCustomTypeSerializer<TWriter>(), value, alwaysWrite, context, notNullableOverride);
    }

    public DataNode WriteValue(Type type, object? value, bool alwaysWrite = false, ISerializationContext? context = null, bool notNullableOverride = false)
    {
        if (value == null)
        {
            CanWriteNullCheck(type, notNullableOverride);

            return NullNode();
        }

        return GetOrCreateWriteBoxingDelegate(type, notNullableOverride)(value, alwaysWrite, context);
    }

    private void CanWriteNullCheck(Type type, bool notNullableOverride)
    {
        if (!type.IsNullable() || notNullableOverride)
        {
            throw new NullNotAllowedException();
        }
    }
}
