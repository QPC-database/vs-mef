﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.Serialization.Formatters.Binary;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.Composition.Reflection;
    using Validation;

    public class CachedComposition : ICompositionCacheManager, IRuntimeCompositionCacheManager
    {
        private static readonly Encoding TextEncoding = Encoding.UTF8;

        public Task SaveAsync(CompositionConfiguration configuration, Stream cacheStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(configuration, "configuration");
            Requires.NotNull(cacheStream, "cacheStream");
            Requires.Argument(cacheStream.CanWrite, "cacheStream", "Writable stream required.");

            return Task.Run(async delegate
            {
                var compositionRuntime = RuntimeComposition.CreateRuntimeComposition(configuration);

                await this.SaveAsync(compositionRuntime, cacheStream, cancellationToken);
            });
        }

        public Task SaveAsync(RuntimeComposition composition, Stream cacheStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(composition, "composition");
            Requires.NotNull(cacheStream, "cacheStream");
            Requires.Argument(cacheStream.CanWrite, "cacheStream", "Writable stream required.");

            return Task.Run(() =>
            {
                using (var writer = new BinaryWriter(cacheStream, TextEncoding, leaveOpen: true))
                {
                    var context = new SerializationContext(writer);
                    context.Write(composition);
                }
            });
        }

        public Task<RuntimeComposition> LoadRuntimeCompositionAsync(Stream cacheStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            Requires.NotNull(cacheStream, "cacheStream");
            Requires.Argument(cacheStream.CanRead, "cacheStream", "Readable stream required.");

            return Task.Run(() =>
            {
                using (var reader = new BinaryReader(cacheStream, TextEncoding, leaveOpen: true))
                {
                    var context = new SerializationContext(reader);
                    var runtimeComposition = context.ReadRuntimeComposition();
                    return runtimeComposition;
                }
            });
        }

        public async Task<IExportProviderFactory> LoadExportProviderFactoryAsync(Stream cacheStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            var runtimeComposition = await this.LoadRuntimeCompositionAsync(cacheStream, cancellationToken);
            return runtimeComposition.CreateExportProviderFactory();
        }

        private class SerializationContext
        {
            private BinaryReader reader;

            private BinaryWriter writer;

            private Dictionary<object, uint> serializingObjectTable;

            private Dictionary<uint, object> deserializingObjectTable;

            internal SerializationContext(BinaryReader reader)
            {
                Requires.NotNull(reader, "reader");
                this.reader = reader;
                this.deserializingObjectTable = new Dictionary<uint, object>();
            }

            internal SerializationContext(BinaryWriter writer)
            {
                Requires.NotNull(writer, "writer");
                this.writer = writer;
                this.serializingObjectTable = new Dictionary<object, uint>(SmartInterningEqualityComparer.Default);
            }

            [Conditional("DEBUG")]
            private static void Trace(string elementName, Stream stream)
            {
                ////Debug.WriteLine("Serialization: {1,7} {0}", elementName, stream.Position);
            }

            internal void Write(RuntimeComposition compositionRuntime)
            {
                Requires.NotNull(writer, "writer");
                Requires.NotNull(compositionRuntime, "compositionRuntime");
                Trace("RuntimeComposition", writer.BaseStream);

                this.Write(compositionRuntime.Parts, this.Write);
            }

            internal RuntimeComposition ReadRuntimeComposition()
            {
                Requires.NotNull(reader, "reader");
                Trace("RuntimeComposition", reader.BaseStream);

                var parts = this.ReadList(reader, this.ReadRuntimePart);
                return RuntimeComposition.CreateRuntimeComposition(parts);
            }

            private void Write(RuntimeComposition.RuntimeExport export)
            {
                Trace("RuntimeExport", writer.BaseStream);

                if (this.TryPrepareSerializeReusableObject(export))
                {
                    this.Write(export.ContractName);
                    this.Write(export.DeclaringType);
                    this.Write(export.Member);
                    this.Write(export.ExportedValueType);
                    this.Write(export.Metadata);
                }
            }

            private RuntimeComposition.RuntimeExport ReadRuntimeExport()
            {
                Trace("RuntimeExport", reader.BaseStream);

                uint id;
                RuntimeComposition.RuntimeExport value;
                if (this.TryPrepareDeserializeReusableObject(out id, out value))
                {
                    var contractName = this.ReadString();
                    var declaringType = this.ReadTypeRef();
                    var member = this.ReadMemberRef();
                    var exportedValueType = this.ReadTypeRef();
                    var metadata = this.ReadMetadata();

                    value = new RuntimeComposition.RuntimeExport(
                        contractName,
                        declaringType,
                        member,
                        exportedValueType,
                        metadata);
                    this.OnDeserializedReusableObject(id, value);
                }

                return value;
            }

            private void Write(RuntimeComposition.RuntimePart part)
            {
                Trace("RuntimePart", writer.BaseStream);

                this.Write(part.Type);
                this.Write(part.Exports, this.Write);
                this.Write(part.ImportingConstructor);
                this.Write(part.ImportingConstructorArguments, this.Write);
                this.Write(part.ImportingMembers, this.Write);
                this.Write(part.OnImportsSatisfied);
                this.Write(part.SharingBoundary);
            }

            private RuntimeComposition.RuntimePart ReadRuntimePart()
            {
                Trace("RuntimePart", reader.BaseStream);

                var type = this.ReadTypeRef();
                var exports = this.ReadList(reader, this.ReadRuntimeExport);
                var importingCtor = this.ReadConstructorRef();
                var importingCtorArguments = this.ReadList(reader, this.ReadRuntimeImport);
                var importingMembers = this.ReadList(reader, this.ReadRuntimeImport);
                var onImportsSatisfied = this.ReadMethodRef();
                var sharingBoundary = this.ReadString();

                return new RuntimeComposition.RuntimePart(
                    type,
                    importingCtor,
                    importingCtorArguments,
                    importingMembers,
                    exports,
                    onImportsSatisfied,
                    sharingBoundary);
            }

            private void Write(MethodRef methodRef)
            {
                Trace("MethodRef", writer.BaseStream);

                if (methodRef.IsEmpty)
                {
                    writer.Write((byte)0);
                }
                else
                {
                    writer.Write((byte)1);
                    this.Write(methodRef.DeclaringType);
                    writer.Write(methodRef.MetadataToken);
                    this.Write(methodRef.GenericMethodArguments, this.Write);
                }
            }

            private MethodRef ReadMethodRef()
            {
                Trace("MethodRef", reader.BaseStream);

                byte nullCheck = reader.ReadByte();
                if (nullCheck == 1)
                {
                    var declaringType = this.ReadTypeRef();
                    var metadataToken = reader.ReadInt32();
                    var genericMethodArguments = this.ReadList(reader, this.ReadTypeRef);
                    return new MethodRef(declaringType, metadataToken, genericMethodArguments.ToImmutableArray());
                }
                else
                {
                    return default(MethodRef);
                }
            }

            private void Write(MemberRef memberRef)
            {
                Trace("MemberRef", writer.BaseStream);

                if (memberRef.IsConstructor)
                {
                    writer.Write((byte)1);
                    this.Write(memberRef.Constructor);
                }
                else if (memberRef.IsField)
                {
                    writer.Write((byte)2);
                    this.Write(memberRef.Field);
                }
                else if (memberRef.IsProperty)
                {
                    writer.Write((byte)3);
                    this.Write(memberRef.Property);
                }
                else if (memberRef.IsMethod)
                {
                    writer.Write((byte)4);
                    this.Write(memberRef.Method);
                }
                else
                {
                    writer.Write((byte)0);
                }
            }

            private MemberRef ReadMemberRef()
            {
                Trace("MemberRef", reader.BaseStream);

                int kind = reader.ReadByte();
                switch (kind)
                {
                    case 0:
                        return default(MemberRef);
                    case 1:
                        return new MemberRef(this.ReadConstructorRef());
                    case 2:
                        return new MemberRef(this.ReadFieldRef());
                    case 3:
                        return new MemberRef(this.ReadPropertyRef());
                    case 4:
                        return new MemberRef(this.ReadMethodRef());
                    default:
                        throw new NotSupportedException();
                }
            }

            private void Write(PropertyRef propertyRef)
            {
                Trace("PropertyRef", writer.BaseStream);

                this.Write(propertyRef.DeclaringType);
                writer.Write(propertyRef.MetadataToken);

                byte flags = 0;
                flags |= propertyRef.GetMethodMetadataToken.HasValue ? (byte)0x1 : (byte)0x0;
                flags |= propertyRef.SetMethodMetadataToken.HasValue ? (byte)0x2 : (byte)0x0;
                writer.Write(flags);

                if (propertyRef.GetMethodMetadataToken.HasValue)
                {
                    writer.Write(propertyRef.GetMethodMetadataToken.Value);
                }

                if (propertyRef.SetMethodMetadataToken.HasValue)
                {
                    writer.Write(propertyRef.SetMethodMetadataToken.Value);
                }
            }

            private PropertyRef ReadPropertyRef()
            {
                Trace("PropertyRef", reader.BaseStream);

                var declaringType = this.ReadTypeRef();
                var metadataToken = reader.ReadInt32();

                byte flags = reader.ReadByte();
                int? getter = null, setter = null;
                if ((flags & 0x1) != 0)
                {
                    getter = reader.ReadInt32();
                }

                if ((flags & 0x2) != 0)
                {
                    setter = reader.ReadInt32();
                }

                return new PropertyRef(
                    declaringType,
                    metadataToken,
                    getter,
                    setter);
            }

            private void Write(FieldRef fieldRef)
            {
                Trace("FieldRef", writer.BaseStream);

                writer.Write(!fieldRef.IsEmpty);
                if (!fieldRef.IsEmpty)
                {
                    this.Write(fieldRef.AssemblyName);
                    writer.Write(fieldRef.MetadataToken);
                }
            }

            private FieldRef ReadFieldRef()
            {
                Trace("FieldRef", reader.BaseStream);

                if (reader.ReadBoolean())
                {
                    var assemblyName = this.ReadAssemblyName();
                    int metadataToken = reader.ReadInt32();
                    return new FieldRef(assemblyName, metadataToken);
                }
                else
                {
                    return default(FieldRef);
                }
            }

            private void Write(ParameterRef parameterRef)
            {
                Trace("ParameterRef", writer.BaseStream);

                writer.Write(!parameterRef.IsEmpty);
                if (!parameterRef.IsEmpty)
                {
                    this.Write(parameterRef.AssemblyName);
                    writer.Write(parameterRef.MethodMetadataToken);
                    writer.Write((byte)parameterRef.ParameterIndex);
                }
            }

            private ParameterRef ReadParameterRef()
            {
                Trace("ParameterRef", reader.BaseStream);

                if (reader.ReadBoolean())
                {
                    var assemblyName = this.ReadAssemblyName();
                    int metadataToken = reader.ReadInt32();
                    var parameterIndex = reader.ReadByte();
                    return new ParameterRef(assemblyName, metadataToken, parameterIndex);
                }
                else
                {
                    return default(ParameterRef);
                }
            }

            private void Write(RuntimeComposition.RuntimeImport import)
            {
                Trace("RuntimeImport", writer.BaseStream);

                writer.Write(import.ImportingMemberRef.IsEmpty ? (byte)2 : (byte)1);
                if (import.ImportingMemberRef.IsEmpty)
                {
                    this.Write(import.ImportingParameterRef);
                }
                else
                {
                    this.Write(import.ImportingMemberRef);
                }

                this.Write(import.Cardinality);
                this.Write(import.SatisfyingExports, this.Write);
                writer.Write(import.IsNonSharedInstanceRequired);
                this.Write(import.Metadata);
                this.Write(import.ExportFactory);
                this.Write(import.ExportFactorySharingBoundaries, this.Write);
            }

            private RuntimeComposition.RuntimeImport ReadRuntimeImport()
            {
                Trace("RuntimeImport", reader.BaseStream);

                byte kind = reader.ReadByte();
                MemberRef importingMember = default(MemberRef);
                ParameterRef importingParameter = default(ParameterRef);
                switch (kind)
                {
                    case 1:
                        importingMember = this.ReadMemberRef();
                        break;
                    case 2:
                        importingParameter = this.ReadParameterRef();
                        break;
                    default:
                        throw new NotSupportedException();
                }

                var cardinality = this.ReadImportCardinality();
                var satisfyingExports = this.ReadList(reader, this.ReadRuntimeExport);
                bool isNonSharedInstanceRequired = reader.ReadBoolean();
                var metadata = this.ReadMetadata();
                var exportFactory = this.ReadTypeRef();
                var exportFactorySharingBoundaries = this.ReadList(reader, this.ReadString);

                return importingMember.IsEmpty
                    ? new RuntimeComposition.RuntimeImport(
                        importingParameter,
                        cardinality,
                        satisfyingExports,
                        isNonSharedInstanceRequired,
                        metadata,
                        exportFactory,
                        exportFactorySharingBoundaries)
                    : new RuntimeComposition.RuntimeImport(
                        importingMember,
                        cardinality,
                        satisfyingExports,
                        isNonSharedInstanceRequired,
                        metadata,
                        exportFactory,
                        exportFactorySharingBoundaries);
            }

            private void Write(ConstructorRef constructorRef)
            {
                Trace("ConstructorRef", writer.BaseStream);

                this.Write(constructorRef.DeclaringType);
                writer.Write(constructorRef.MetadataToken);
            }

            private ConstructorRef ReadConstructorRef()
            {
                Trace("ConstructorRef", reader.BaseStream);

                var declaringType = this.ReadTypeRef();
                var metadataToken = reader.ReadInt32();

                return new ConstructorRef(
                    declaringType,
                    metadataToken);
            }

            private void Write(TypeRef typeRef)
            {
                Trace("TypeRef", writer.BaseStream);

                if (this.TryPrepareSerializeReusableObject(typeRef))
                {
                    this.Write(typeRef.AssemblyName);
                    writer.Write(typeRef.MetadataToken);
                    writer.Write(typeRef.IsArray);
                    writer.Write((byte)typeRef.GenericTypeParameterCount);
                    this.Write(typeRef.GenericTypeArguments, this.Write);
                }
            }

            private TypeRef ReadTypeRef()
            {
                Trace("TypeRef", reader.BaseStream);

                uint id;
                TypeRef value;
                if (this.TryPrepareDeserializeReusableObject(out id, out value))
                {
                    var assemblyName = this.ReadAssemblyName();
                    var metadataToken = reader.ReadInt32();
                    bool isArray = reader.ReadBoolean();
                    int genericTypeParameterCount = reader.ReadByte();
                    var genericTypeArguments = this.ReadList(reader, this.ReadTypeRef);
                    value = TypeRef.Get(assemblyName, metadataToken, isArray, genericTypeParameterCount, genericTypeArguments.ToImmutableArray());
                    this.OnDeserializedReusableObject(id, value);
                }

                return value;
            }

            private void Write(AssemblyName assemblyName)
            {
                Trace("AssemblyName", writer.BaseStream);

                if (this.TryPrepareSerializeReusableObject(assemblyName))
                {
                    this.Write(assemblyName.FullName);
                    this.Write(assemblyName.CodeBase);
                }
            }

            private AssemblyName ReadAssemblyName()
            {
                Trace("AssemblyName", reader.BaseStream);

                uint id;
                AssemblyName value;
                if (this.TryPrepareDeserializeReusableObject(out id, out value))
                {
                    string fullName = this.ReadString();
                    string codeBase = this.ReadString();
                    value = new AssemblyName(fullName) { CodeBase = codeBase };
                    this.OnDeserializedReusableObject(id, value);
                }

                return value;
            }

            private void Write(string value)
            {
                Trace("String", writer.BaseStream);

                if (this.TryPrepareSerializeReusableObject(value))
                {
                    writer.Write(value);
                }
            }

            private string ReadString()
            {
                Trace("String", reader.BaseStream);

                uint id;
                string value;
                if (this.TryPrepareDeserializeReusableObject(out id, out value))
                {
                    value = reader.ReadString();
                    this.OnDeserializedReusableObject(id, value);
                }

                return value;
            }

            private void WriteCompressedUInt(uint value)
            {
                CompressedUInt.WriteCompressedUInt(writer, value);
            }

            private uint ReadCompressedUInt()
            {
                return CompressedUInt.ReadCompressedUInt(reader);
            }

            /// <summary>
            /// Prepares the object for referential sharing in the serialization stream.
            /// </summary>
            /// <param name="value">The value that may be serialized more than once.</param>
            /// <returns><c>true</c> if the object should be serialized; otherwise <c>false</c>.</returns>
            private bool TryPrepareSerializeReusableObject(object value)
            {
                uint id;
                bool result;
                if (value == null)
                {
                    id = 0;
                    result = false;
                }
                else if (this.serializingObjectTable.TryGetValue(value, out id))
                {
                    // The object has already been serialized.
                    result = false;
                }
                else
                {
                    this.serializingObjectTable.Add(value, id = (uint)this.serializingObjectTable.Count + 1);
                    result = true;
                }

                this.WriteCompressedUInt(id);
                return result;
            }

            /// <summary>
            /// Gets an object that has already been deserialized, if available.
            /// </summary>
            /// <param name="id">Receives the ID of the object.</param>
            /// <param name="value">Receives the value of the object, if available.</param>
            /// <returns><c>true</c> if the caller should deserialize the object; <c>false</c> if the object is in <paramref name="value"/>.</returns>
            private bool TryPrepareDeserializeReusableObject<T>(out uint id, out T value)
                where T : class
            {
                id = this.ReadCompressedUInt();
                if (id == 0)
                {
                    value = null;
                    return false;
                }

                object valueObject;
                bool result = !this.deserializingObjectTable.TryGetValue(id, out valueObject);
                value = (T)valueObject;
                return result;
            }

            private void OnDeserializedReusableObject(uint id, object value)
            {
                this.deserializingObjectTable.Add(id, value);
            }

            private void Write<T>(IReadOnlyCollection<T> list, Action<T> itemWriter)
            {
                Requires.NotNull(list, "list");
                Trace("List<" + typeof(T).Name + ">", writer.BaseStream);

                this.WriteCompressedUInt((uint)list.Count);
                foreach (var item in list)
                {
                    itemWriter(item);
                }
            }

            private void Write(Array list, Action<object> itemWriter)
            {
                Requires.NotNull(list, "list");
                Trace((list != null ? list.GetType().GetElementType().Name : "null") + "[]", writer.BaseStream);

                this.WriteCompressedUInt((uint)list.Length);
                foreach (var item in list)
                {
                    itemWriter(item);
                }
            }

            private IReadOnlyList<T> ReadList<T>(BinaryReader reader, Func<T> itemReader)
            {
                Trace("List<" + typeof(T).Name + ">", reader.BaseStream);

                uint count = this.ReadCompressedUInt();
                if (count > 0xffff)
                {
                    // Probably either file corruption or a bug in serialization.
                    // Let's not take untold amounts of memory by throwing out suspiciously large lengths.
                    throw new NotSupportedException();
                }

                var list = new T[count];
                for (int i = 0; i < list.Length; i++)
                {
                    list[i] = itemReader();
                }

                return list;
            }

            private Array ReadArray(BinaryReader reader, Func<object> itemReader, Type elementType)
            {
                Trace("List<" + elementType.Name + ">", reader.BaseStream);

                uint count = this.ReadCompressedUInt();
                if (count > 0xffff)
                {
                    // Probably either file corruption or a bug in serialization.
                    // Let's not take untold amounts of memory by throwing out suspiciously large lengths.
                    throw new NotSupportedException();
                }

                var list = Array.CreateInstance(elementType, count);
                for (int i = 0; i < list.Length; i++)
                {
                    list.SetValue(itemReader(), i);
                }

                return list;
            }

            private void Write(IReadOnlyDictionary<string, object> metadata)
            {
                Trace("Metadata", writer.BaseStream);

                this.WriteCompressedUInt((uint)metadata.Count);
                foreach (var entry in metadata)
                {
                    this.Write(entry.Key);

                    // Special case values of type Type or Type[] to avoid defeating lazy load later.
                    // We deserialize keeping the replaced TypeRef values so that they can be resolved
                    // at the last possible moment by the metadata view at runtime.
                    // Check out the ReadMetadata below, how it wraps the return value.
                    if (entry.Value is Type)
                    {
                        this.WriteObject(TypeRef.Get((Type)entry.Value));
                    }
                    else if (entry.Value is Type[])
                    {
                        this.WriteObject(((Type[])entry.Value).Select(TypeRef.Get).ToArray());
                    }
                    else
                    {
                        this.WriteObject(entry.Value);
                    }
                }
            }

            private IReadOnlyDictionary<string, object> ReadMetadata()
            {
                Trace("Metadata", reader.BaseStream);

                uint count = this.ReadCompressedUInt();
                var metadata = ImmutableDictionary<string, object>.Empty;

                if (count > 0)
                {
                    var builder = metadata.ToBuilder();
                    for (int i = 0; i < count; i++)
                    {
                        string key = this.ReadString();
                        object value = this.ReadObject();
                        builder.Add(key, value);
                    }

                    metadata = builder.ToImmutable();
                }

                return new LazyMetadataWrapper(metadata);
            }

            private void Write(ImportCardinality cardinality)
            {
                Trace("ImportCardinality", writer.BaseStream);

                writer.Write((byte)cardinality);
            }

            private ImportCardinality ReadImportCardinality()
            {
                Trace("ImportCardinality", reader.BaseStream);
                return (ImportCardinality)reader.ReadByte();
            }

            private enum ObjectType : byte
            {
                Null,
                String,
                CreationPolicy,
                Type,
                Array,
                BinaryFormattedObject,
                TypeRef,
            }

            private void WriteObject(object value)
            {
                if (value == null)
                {
                    Trace("Object (null)", writer.BaseStream);
                    this.Write(ObjectType.Null);
                }
                else
                {
                    Type valueType = value.GetType();
                    Trace("Object (" + valueType.Name + ")", writer.BaseStream);
                    if (valueType.IsArray)
                    {
                        Array array = (Array)value;
                        this.Write(ObjectType.Array);
                        this.Write(TypeRef.Get(valueType.GetElementType()));
                        this.Write(array, this.WriteObject);
                    }
                    else if (valueType == typeof(string))
                    {
                        this.Write(ObjectType.String);
                        this.Write((string)value);
                    }
                    else if (valueType == typeof(CreationPolicy)) // TODO: how do we handle arbitrary value types?
                    {
                        this.Write(ObjectType.CreationPolicy);
                        writer.Write((byte)(CreationPolicy)value);
                    }
                    else if (typeof(Type).IsAssignableFrom(valueType))
                    {
                        this.Write(ObjectType.Type);
                        this.Write(TypeRef.Get((Type)value));
                    }
                    else if (typeof(TypeRef) == valueType)
                    {
                        this.Write(ObjectType.TypeRef);
                        this.Write((TypeRef)value);
                    }
                    else
                    {
                        this.Write(ObjectType.BinaryFormattedObject);
                        var formatter = new BinaryFormatter();
                        writer.Flush();
                        formatter.Serialize(writer.BaseStream, value);
                    }
                }
            }

            private object ReadObject()
            {
                Trace("Object", reader.BaseStream);
                ObjectType objectType = this.ReadObjectType();
                switch (objectType)
                {
                    case ObjectType.Null:
                        return null;
                    case ObjectType.Array:
                        Type elementType = this.ReadTypeRef().Resolve();
                        return this.ReadArray(reader, this.ReadObject, elementType);
                    case ObjectType.String:
                        return this.ReadString();
                    case ObjectType.CreationPolicy:
                        return (CreationPolicy)reader.ReadByte();
                    case ObjectType.Type:
                        return this.ReadTypeRef().Resolve();
                    case ObjectType.TypeRef:
                        return this.ReadTypeRef();
                    case ObjectType.BinaryFormattedObject:
                        var formatter = new BinaryFormatter();
                        return formatter.Deserialize(reader.BaseStream);
                    default:
                        throw new NotSupportedException("Unsupported format: " + objectType);
                }
            }

            private void Write(ObjectType type)
            {
                writer.Write((byte)type);
            }

            private ObjectType ReadObjectType()
            {
                var objectType = (ObjectType)reader.ReadByte();
                return objectType;
            }

            /// <summary>
            /// An equality comparer that provides a bit better recognition of objects for better interning.
            /// </summary>
            private class SmartInterningEqualityComparer : IEqualityComparer<object>
            {
                internal static readonly IEqualityComparer<object> Default = new SmartInterningEqualityComparer();

                private static readonly IEqualityComparer<object> Fallback = EqualityComparer<object>.Default;

                private SmartInterningEqualityComparer() { }

                public bool Equals(object x, object y)
                {
                    if (x is AssemblyName && y is AssemblyName)
                    {
                        return ByValueEquality.AssemblyName.Equals((AssemblyName)x, (AssemblyName)y);
                    }

                    return Fallback.Equals(x, y);
                }

                public int GetHashCode(object obj)
                {
                    if (obj is AssemblyName)
                    {
                        return ByValueEquality.AssemblyName.GetHashCode((AssemblyName)obj);
                    }

                    return Fallback.GetHashCode(obj);
                }
            }
        }
    }
}
