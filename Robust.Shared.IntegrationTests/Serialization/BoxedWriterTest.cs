using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Exceptions;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Robust.UnitTesting.Shared.Serialization;

[TestFixture]
public sealed partial class BoxedWriterTest : OurSerializationTest
{
    private delegate DataNode Writer(object value, bool alwaysWrite, ISerializationContext? context);

    private Writer Legacy(Type type, bool notNullable, SerializationManager? manager = null)
    {
        var value = Expression.Parameter(typeof(object));
        var always = Expression.Parameter(typeof(bool));
        var context = Expression.Parameter(typeof(ISerializationContext));
        return Expression.Lambda<Writer>(Expression.Call(Expression.Constant(manager ?? (SerializationManager)Serialization),
            nameof(ISerializationManager.WriteValue), [type], Expression.Convert(value, type), always, context,
            Expression.Constant(notNullable)), value, always, context).Compile();
    }

    private static IEnumerable<TestCaseData> Values()
    {
        yield return new TestCaseData(typeof(int), 123);
        yield return new TestCaseData(typeof(int?), 123);
        yield return new TestCaseData(typeof(string), "мир/世界");
        yield return new TestCaseData(typeof(DayOfWeek), DayOfWeek.Friday);
        yield return new TestCaseData(typeof(List<int>), new List<int> { 1, 4, 9 });
        yield return new TestCaseData(typeof(int[]), new[] { 2, 5 });
        yield return new TestCaseData(typeof(Sample), new Sample());
        yield return new TestCaseData(typeof(object), new Sample { Number = 7 });
    }

    [TestCaseSource(nameof(Values))]
    public void BoxedDocumentMatchesOldAdapter(Type type, object value)
    {
        foreach (var alwaysWrite in new[] { false, true })
        {
            var expected = Legacy(type, false)(value, alwaysWrite, null);
            var actual = Serialization.WriteValue(type, value, alwaysWrite);
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CachedAdapterUsesCurrentContextAndAlwaysWrite(bool alwaysWrite)
    {
        Serialization.WriteValue(typeof(int), 12); // Cache the ordinary path first.
        var context = new IntContext(Serialization);
        var expected = Legacy(typeof(int), false)(12, alwaysWrite, context);
        var actual = Serialization.WriteValue(typeof(int), 12, alwaysWrite, context);
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(((ValueDataNode)actual).Value, Is.EqualTo("context:12"));
        Assert.That(context.LastAlwaysWrite, Is.EqualTo(alwaysWrite));
        Assert.That(context.LastContext, Is.SameAs(context));
    }

    [Test]
    public void ErrorsRemainDirectAndNullableOverridesRemainIndependent()
    {
        Assert.Throws<InvalidCastException>(() => Serialization.WriteValue(typeof(int), "wrong"));
        var context = new IntContext(Serialization) { Fail = true };
        Assert.Throws<InvalidOperationException>(() => Serialization.WriteValue(typeof(int), 12, context: context));
        Assert.That(Serialization.WriteValue(typeof(int?), null), Is.EqualTo(ValueDataNode.Null()));
        Assert.Throws<NullNotAllowedException>(() => Serialization.WriteValue(typeof(int?), null, notNullableOverride: true));
        foreach (var flag in new[] { false, true })
            Assert.That(Serialization.WriteValue(typeof(int?), 12, notNullableOverride: flag),
                Is.EqualTo(Legacy(typeof(int?), flag)(12, false, null)));
    }

    [Test]
    public void ColdAdapterPreparationMeasurement()
    {
        var method = typeof(SerializationManager).GetMethod("GetOrCreateWriteBoxingDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var types = new[] { typeof(int), typeof(string), typeof(double), typeof(Guid), typeof(bool) }
            .SelectMany(a => new[] { typeof(int), typeof(string), typeof(double), typeof(Guid), typeof(bool) }
                .Select(b => typeof(Dictionary<,>).MakeGenericType(a, typeof(List<>).MakeGenericType(b)))).ToArray();
        var manager = new SerializationManager();
        Legacy(typeof(decimal), false, manager);
        method.Invoke(manager, [typeof(decimal), false]);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        foreach (var type in types) Legacy(type, false, manager);
        var oldMs = timer.Elapsed.TotalMilliseconds;
        var oldBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        foreach (var type in types)
        {
            var adapter = method.Invoke(manager, [type, false]);
            Assert.That(method.Invoke(manager, [type, false]), Is.SameAs(adapter));
        }
        var newMs = timer.Elapsed.TotalMilliseconds;
        var newBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TestContext.Out.WriteLine($"25 cold boxed adapters: old={oldMs:F3}ms/{oldBytes}B; new={newMs:F3}ms/{newBytes}B (new includes cache-hit assertions)");
    }

    [DataDefinition]
    public sealed partial class Sample
    {
        [DataField] public int Number = 3;
    }

    private sealed class IntContext : ISerializationContext, ITypeWriter<int>
    {
        public SerializationManager.SerializerProvider SerializerProvider { get; }
        public bool WritingReadingPrototypes => false;
        public bool LastAlwaysWrite, Fail;
        public ISerializationContext? LastContext;
        public IntContext(ISerializationManager manager)
        {
            SerializerProvider = new(manager);
            SerializerProvider.RegisterSerializer(this);
        }
        public DataNode Write(ISerializationManager manager, int value, IDependencyCollection dependencies,
            bool alwaysWrite = false, ISerializationContext? context = null)
        {
            if (Fail) throw new InvalidOperationException("writer failure");
            LastAlwaysWrite = alwaysWrite;
            LastContext = context;
            return new ValueDataNode("context:" + value);
        }
    }
}
