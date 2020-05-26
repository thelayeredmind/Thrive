using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
///   Main JSON conversion class for Thrive handling all our custom stuff
/// </summary>
public class ThriveJsonConverter : IDisposable
{
    private static readonly ThriveJsonConverter InstanceValue =
        new ThriveJsonConverter(new SaveContext(SimulationParameters.Instance));

    private readonly SaveContext context;

    private readonly JsonConverter[] thriveConverters;
    private readonly List<JsonConverter> thriveConvertersDynamicDeserialize;

    private ThreadLocal<JsonSerializerSettings> currentJsonSettings = new ThreadLocal<JsonSerializerSettings>();
    private bool disposed;

    // private IReferenceResolver referenceResolver = new Default;

    private ThriveJsonConverter(SaveContext context)
    {
        this.context = context;

        // All of the thrive serializers need to be registered here
        thriveConverters = new JsonConverter[]
        {
            new RegistryTypeConverter(context),
            new GodotColorConverter(),
            new GodotBasisConverter(),
            new SystemVector4ArrayConverter(),

            // TODO: is this one needed? It doesn't have any special stuff left
            new SpeciesConverter(context),
            new CompoundCloudPlaneConverter(context),
            new CompoundBagConverter(context),

            // Converter for all types with the specific attribute for this to be enabled
            new DefaultThriveJSONConverter(context),

            // Specific Godot Node converter types

            // Fallback Godot Node converter
            new BaseNodeConverter(context),
        };

        thriveConvertersDynamicDeserialize = new List<JsonConverter> { new DynamicDeserializeObjectConverter(context) };
        thriveConvertersDynamicDeserialize.AddRange(thriveConverters);
    }

    public static ThriveJsonConverter Instance => InstanceValue;

    public string SerializeObject(object o)
    {
        return PerformWithSettings((settings) => JsonConvert.SerializeObject(o, Constants.SAVE_FORMATTING, settings));
    }

    public T DeserializeObject<T>(string json)
    {
        return PerformWithSettings((settings) => JsonConvert.DeserializeObject<T>(json, settings));
    }

    /// <summary>
    ///   Deserializes a fully dynamic object from json (object type is defined only in the json)
    /// </summary>
    /// <param name="json">JSON text to parse</param>
    /// <returns>The created object</returns>
    /// <exception cref="JsonException">If invalid json or the dynamic type is not allowed</exception>
    public object DeserializeObjectDynamic(string json)
    {
        return PerformWithSettings((settings) =>
        {
            // enable hack conversion
            settings.Converters = thriveConvertersDynamicDeserialize;

            try
            {
                return JsonConvert.DeserializeObject<object>(json, settings);
            }
            finally
            {
                // disable hack conversion
                settings.Converters = thriveConverters;
            }
        });
    }

    // /// <summary>
    // ///   Recursively resolves
    // /// </summary>
    // /// <param name="obj">Object to start looking at properties in</param>
    // /// <param name="context"></param>
    // public void ResolveLoadables(object obj, ISaveContext context)
    // {
    //
    // }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                currentJsonSettings.Dispose();
            }

            disposed = true;
        }
    }

    private JsonSerializerSettings CreateSettings()
    {
        var referenceResolver = new ReferenceResolver();

        return new JsonSerializerSettings()
        {
            // PreserveReferencesHandling = PreserveReferencesHandling.Objects,

            // We need to be careful to not deserialize untrusted data with this serializer
            TypeNameHandling = TypeNameHandling.Auto,

            // This blocks most types from using typename handling
            SerializationBinder = new SerializationBinder(),

            Converters = thriveConverters,

            ReferenceResolverProvider = () => referenceResolver,
        };
    }

    private T PerformWithSettings<T>(Func<JsonSerializerSettings, T> func)
    {
        JsonSerializerSettings settings;

        bool recursive = false;

        if (currentJsonSettings.Value != null)
        {
            // This is a recursive call
            recursive = true;
            settings = currentJsonSettings.Value;
        }
        else
        {
            settings = CreateSettings();
            currentJsonSettings.Value = settings;
        }

        try
        {
            return func(settings);
        }
        finally
        {
            if (!recursive)
                currentJsonSettings.Value = null;
        }
    }
}

/// <summary>
///   Base for all the thrive json converter types.
///   this is used to allow access to the global information that shouldn't be saved.
/// </summary>
public abstract class BaseThriveConverter : JsonConverter
{
    public readonly ISaveContext Context;

    // ref handling approach from: https://stackoverflow.com/a/53716866/4371508
    private const string REF_PROPERTY = "$ref";
    private const string ID_PROPERTY = "$id";

    // type handling approach from: https://stackoverflow.com/a/29822170/4371508
    // and https://stackoverflow.com/a/29826959/4371508
    private const string TYPE_PROPERTY = "$type";

    protected BaseThriveConverter(ISaveContext context)
    {
        Context = context;
    }

    /// <summary>
    ///   These need to always be able to read as we use json for saving so it makes no sense to
    ///   have a one-way converter
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    ///   Finds the actual key for a thing ignoring different cases
    /// </summary>
    /// <param name="items">Items to check keys for</param>
    /// <param name="candidateKey">Key to test with if it can be found</param>
    /// <returns>The best found key</returns>
    public static string DetermineKey(JObject items, string candidateKey)
    {
        if (items.ContainsKey(candidateKey))
            return candidateKey;

        foreach (var item in items)
        {
            if (item.Key.Equals(candidateKey, StringComparison.OrdinalIgnoreCase))
            {
                return item.Key;
            }
        }

        // No matches
        return candidateKey;
    }

    public static IEnumerable<FieldInfo> FieldsOf(object value)
    {
        var fields = value.GetType().GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where((p) => p.CustomAttributes.All(
            a => a.AttributeType != typeof(JsonIgnoreAttribute) &&
                a.AttributeType != typeof(CompilerGeneratedAttribute)));

        // Ignore fields that aren't public unless it has JsonPropertyAttribute
        return fields.Where((p) =>
            (p.IsPublic && !p.IsInitOnly) ||
            p.CustomAttributes.Any((a) => a.AttributeType == typeof(JsonPropertyAttribute)));
    }

    public static IEnumerable<PropertyInfo> PropertiesOf(object value)
    {
        var properties = value.GetType().GetProperties(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(
            (p) => p.CustomAttributes.All(
                a => a.AttributeType != typeof(JsonIgnoreAttribute)));

        // Ignore properties that don't have a public setter unless it has JsonPropertyAttribute
        return properties.Where((p) => p.GetSetMethod() != null ||
            p.CustomAttributes.Any((a) => a.AttributeType == typeof(JsonPropertyAttribute)));
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
        JsonSerializer serializer)
    {
        var customRead = ReadCustomJson(reader, objectType, existingValue, serializer);

        if (customRead.performed)
            return customRead.read;

        if (reader.TokenType != JsonToken.StartObject)
        {
            return null;
        }

        var item = JObject.Load(reader);

        // Detect ref to already loaded object
        var refId = item[REF_PROPERTY];

        if (refId != null)
        {
            var reference = serializer.ReferenceResolver.ResolveReference(serializer, refId.Value<string>());
            if (reference != null)
                return reference;
        }

        var objId = item[ID_PROPERTY];

        // Detect dynamic typing
        var type = item[TYPE_PROPERTY];

        if (type != null)
        {
            if (serializer.TypeNameHandling != TypeNameHandling.None)
            {
                var parts = type.Value<string>().Split(',').Select(p => p.Trim()).ToList();
                if (parts.Count != 2 && parts.Count != 1)
                    throw new JsonException("invalid $type format");

                // Change to loading the other type
                objectType = serializer.SerializationBinder.BindToType(
                    parts.Count > 1 ? parts[1] : null, parts[0]);
            }
        }

        if (objectType == typeof(DynamicDeserializeObjectConverter))
            throw new JsonException("Dynamic dummy deserialize used object didn't specify type");

        // Find a constructor we can call
        ConstructorInfo constructor = null;

        foreach (var candidate in objectType.GetConstructors())
        {
            if (candidate.ContainsGenericParameters)
                continue;

            bool canUseThis = true;

            // Check do we have all the needed parameters
            foreach (var param in candidate.GetParameters())
            {
                if (!item.ContainsKey(DetermineKey(item, param.Name)))
                {
                    canUseThis = false;
                    break;
                }
            }

            if (!canUseThis)
                continue;

            if (constructor == null || constructor.GetParameters().Length < candidate.GetParameters().Length)
                constructor = candidate;
        }

        if (constructor == null)
        {
            throw new JsonException($"no suitable constructor found for current type: {objectType}");
        }

        HashSet<string> alreadyConsumedItems = new HashSet<string>();

        foreach (var param in constructor.GetParameters())
        {
            alreadyConsumedItems.Add(DetermineKey(item, param.Name));
        }

        // Load constructor params
        object[] constructorArgs = constructor.GetParameters()
            .Select((p) => ReadMember(DetermineKey(item, p.Name),
                p.ParameterType, item, null, reader, serializer)).ToArray();

        var instance = constructor.Invoke(constructorArgs);

        // Store the instance before loading properties to not break on recursive references
        if (objId != null)
        {
            serializer.ReferenceResolver.AddReference(serializer, objId.Value<string>(), instance);
        }

        bool Skip(string name, string key)
        {
            return SkipMember(name) || alreadyConsumedItems.Contains(key);
        }

        foreach (var field in FieldsOf(instance))
        {
            var name = DetermineKey(item, field.Name);
            if (Skip(field.Name, name))
                continue;

            field.SetValue(instance, ReadMember(name, field.FieldType, item, instance, reader, serializer));
        }

        foreach (var property in PropertiesOf(instance))
        {
            var name = DetermineKey(item, property.Name);
            if (Skip(property.Name, name))
                continue;

            var set = property.GetSetMethodOnDeclaringType();

            if (set == null)
            {
                throw new InvalidOperationException(
                    $"Json property used on a property ({name})that has no (private) setter");
            }

            set.Invoke(instance, new object[]
            {
                ReadMember(name, property.PropertyType, item, instance, reader,
                    serializer),
            });
        }

        ReadCustomExtraFields(item, instance, reader, objectType, existingValue, serializer);

        return instance;
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        if (value == null)
        {
            serializer.Serialize(writer, null);
            return;
        }

        if (WriteCustomJson(writer, value, serializer))
            return;

        var type = value.GetType();

        var contract = serializer.ContractResolver.ResolveContract(type);

        bool reference = serializer.PreserveReferencesHandling != PreserveReferencesHandling.None ||
            (contract.IsReference.HasValue && contract.IsReference.Value);

        writer.WriteStartObject();

        if (reference && serializer.ReferenceResolver.IsReferenced(serializer, value))
        {
            // Already written, just write the ref
            writer.WritePropertyName(REF_PROPERTY);
            writer.WriteValue(serializer.ReferenceResolver.GetReference(serializer, value));
        }
        else
        {
            if (reference)
            {
                writer.WritePropertyName(ID_PROPERTY);
                writer.WriteValue(serializer.ReferenceResolver.GetReference(serializer, value));
            }

            // Dynamic typing
            if (serializer.TypeNameHandling != TypeNameHandling.None)
            {
                // Write the type of the instance always as we can't detect if the value matches the type of the field
                // We can at least check that the actual type is a subclass of something allowing dynamic typing
                if (type.BaseType != null && type.BaseType.CustomAttributes.Any((attr) =>
                    attr.AttributeType == typeof(JSONDynamicTypeAllowedAttribute)))
                {
                    writer.WritePropertyName(TYPE_PROPERTY);

                    var typeStr = $"{type.FullName}, {type.Assembly.GetName().Name}";

                    writer.WriteValue(typeStr);
                }
            }

            // First time writing, write all fields and properties
            foreach (var field in FieldsOf(value))
            {
                WriteMember(field.Name, field.GetValue(value), field.FieldType, type, writer, serializer);
            }

            foreach (var property in PropertiesOf(value))
            {
                // Reading some godot properties already triggers problems, so we ignore those here
                if (SkipIfGodotNodeType(property.Name, type))
                    continue;

                WriteMember(property.Name, property.GetValue(value, null), property.PropertyType, type, writer,
                    serializer);
            }

            WriteCustomExtraFields(writer, value, serializer);
        }

        writer.WriteEndObject();
    }

    protected virtual (object read, bool performed) ReadCustomJson(JsonReader reader, Type objectType,
        object existingValue, JsonSerializer serializer)
    {
        return (null, false);
    }

    protected virtual bool WriteCustomJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        return false;
    }

    /// <summary>
    ///   Default member writer for thrive types. Has special handling for some Thrive types,
    ///   others use default serializers
    /// </summary>
    protected virtual void WriteMember(string name, object memberValue, Type memberType, Type objectType,
        JsonWriter writer,
        JsonSerializer serializer)
    {
        if (SkipMember(name))
            return;

        writer.WritePropertyName(name);

        // Special handle types (none currently)

        // Use default serializer on everything else
        serializer.Serialize(writer, memberValue);
    }

    protected virtual void WriteCustomExtraFields(JsonWriter writer, object value, JsonSerializer serializer)
    {
    }

    protected virtual object ReadMember(string name, Type memberType, JObject item, object instance, JsonReader reader,
        JsonSerializer serializer)
    {
        var value = item[name];

        // Special handle types (none currently)

        // Use default get on everything else
        return value?.ToObject(memberType, serializer);
    }

    protected virtual void ReadCustomExtraFields(JObject item, object instance, JsonReader reader, Type objectType,
        object existingValue, JsonSerializer serializer)
    {
    }

    protected virtual bool SkipMember(string name)
    {
        return false;
    }

    protected virtual bool SkipIfGodotNodeType(string name, Type type)
    {
        if (typeof(Node).IsAssignableFrom(type) && BaseNodeConverter.IsIgnoredGodotNodeMember(name))
            return true;

        return false;
    }
}

/// <summary>
///   When a class has this attribute DefaultThriveJSONConverter is used to serialize it
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class UseThriveSerializerAttribute : Attribute
{
}

/// <summary>
///   Custom serializer for all Thrive types that don't need any special handling. They need to have the attribute
///   UseThriveSerializerAttribute to be detected
/// </summary>
internal class DefaultThriveJSONConverter : BaseThriveConverter
{
    public DefaultThriveJSONConverter(ISaveContext context) : base(context)
    {
    }

    public DefaultThriveJSONConverter() : base(new SaveContext())
    {
    }

    public override bool CanConvert(Type objectType)
    {
        // Types with out custom attribute are supported
        return objectType.CustomAttributes.Any(
            (attr) => attr.AttributeType == typeof(UseThriveSerializerAttribute));
    }
}
