using System.Xml.Serialization;
using JunimoGate.Tests;
using StardewModdingAPI.Mobile.Serialization;

internal static class AndroidSaveSerializerRegistryTests
{
    public static void UsesTheNativeSerializerUntilOverridden()
    {
        FakeSaveSerializer.Reset();
        var registry = AndroidSaveSerializerRegistry.Create(
            typeof(FakeSaveSerializer),
            FakeSaveSerializer.GetSerializer);

        XmlSerializer native = registry.Get(typeof(AndroidSaveSerializerRegistryTestRoot));
        TestHarness.True(ReferenceEquals(native, FakeSaveSerializer.GetSerializer(typeof(AndroidSaveSerializerRegistryTestRoot))));
    }

    public static void PublishesOverridesThroughTheGameLookup()
    {
        FakeSaveSerializer.Reset();
        var registry = AndroidSaveSerializerRegistry.Create(
            typeof(FakeSaveSerializer),
            FakeSaveSerializer.GetSerializer);
        var replacement = new XmlSerializer(typeof(AndroidSaveSerializerRegistryTestRoot));

        registry.Set(typeof(AndroidSaveSerializerRegistryTestRoot), replacement);

        TestHarness.True(ReferenceEquals(replacement, registry.Get(typeof(AndroidSaveSerializerRegistryTestRoot))));
        TestHarness.True(ReferenceEquals(replacement, FakeSaveSerializer.GetSerializer(typeof(AndroidSaveSerializerRegistryTestRoot))));
    }

    public static void RollsBackAnUnobservableOverride()
    {
        FakeSaveSerializer.Reset();
        XmlSerializer native = FakeSaveSerializer.GetSerializer(typeof(AndroidSaveSerializerRegistryTestRoot));
        var registry = AndroidSaveSerializerRegistry.Create(
            typeof(FakeSaveSerializer),
            _ => native);
        var replacement = new XmlSerializer(typeof(AndroidSaveSerializerRegistryTestRoot));

        TestHarness.Throws<InvalidOperationException>(() =>
            registry.Set(typeof(AndroidSaveSerializerRegistryTestRoot), replacement));

        TestHarness.True(ReferenceEquals(native, FakeSaveSerializer.GetSerializer(typeof(AndroidSaveSerializerRegistryTestRoot))));
    }

    public static void RejectsAnAmbiguousCacheShape()
    {
        TestHarness.Throws<InvalidOperationException>(() => AndroidSaveSerializerRegistry.Create(
            typeof(AmbiguousSaveSerializer),
            _ => new XmlSerializer(typeof(AndroidSaveSerializerRegistryTestRoot))));
    }

    private static class FakeSaveSerializer
    {
        private static readonly Dictionary<Type, XmlSerializer> Serializers = [];

        public static XmlSerializer GetSerializer(Type type)
        {
            if (!Serializers.TryGetValue(type, out XmlSerializer? serializer))
            {
                serializer = new XmlSerializer(type);
                Serializers.Add(type, serializer);
            }

            return serializer;
        }

        public static void Reset() => Serializers.Clear();
    }

    private static class AmbiguousSaveSerializer
    {
        private static readonly Dictionary<Type, XmlSerializer> First = [];
        private static readonly Dictionary<Type, XmlSerializer> Second = [];
    }
}

public sealed class AndroidSaveSerializerRegistryTestRoot
{
    public string? Value { get; set; }
}
