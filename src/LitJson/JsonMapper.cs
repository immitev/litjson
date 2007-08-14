#region Header
/**
 * JsonMapper.cs
 *   JSON to .Net object and object to JSON conversions.
 *
 * The authors disclaim copyright to this source code. For more details, see
 * the COPYING file included with this distribution.
 **/
#endregion


using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;


namespace LitJson
{
    internal struct PropertyMetadata
    {
        public MemberInfo Info;
        public bool       IsField;
        public Type       Type;
    }


    internal struct ArrayMetadata
    {
        private Type element_type;
        private bool is_array;
        private bool is_list;


        public Type ElementType {
            get {
                if (element_type == null)
                    return typeof (JsonData);

                return element_type;
            }

            set { element_type = value; }
        }

        public bool IsArray {
            get { return is_array; }
            set { is_array = value; }
        }

        public bool IsList {
            get { return is_list; }
            set { is_list = value; }
        }
    }


    internal struct ObjectMetadata
    {
        private Type element_type;
        private bool is_dictionary;

        private IDictionary<string, PropertyMetadata> properties;


        public Type ElementType {
            get {
                if (element_type == null)
                    return typeof (JsonData);

                return element_type;
            }

            set { element_type = value; }
        }

        public bool IsDictionary {
            get { return is_dictionary; }
            set { is_dictionary = value; }
        }

        public IDictionary<string, PropertyMetadata> Properties {
            get { return properties; }
            set { properties = value; }
        }
    }


    public delegate IJsonWrapper WrapperFactory ();


    public class JsonMapper
    {
        #region Fields
        private static IDictionary<Type, ArrayMetadata> array_metadata;
        private static readonly object array_metadata_lock = new Object ();

        private static IDictionary<Type,
                IDictionary<Type, MethodInfo>> conv_ops;
        private static readonly object conv_ops_lock = new Object ();

        private static IDictionary<Type, ObjectMetadata> object_metadata;
        private static readonly object object_metadata_lock = new Object ();

        private static IDictionary<Type,
                IList<PropertyMetadata>> type_properties;
        private static readonly object type_properties_lock = new Object ();
        #endregion


        static JsonMapper ()
        {
            array_metadata = new Dictionary<Type, ArrayMetadata> ();
            conv_ops = new Dictionary<Type, IDictionary<Type, MethodInfo>> ();
            object_metadata = new Dictionary<Type, ObjectMetadata> ();
            type_properties = new Dictionary<Type,
                            IList<PropertyMetadata>> ();
        }


        #region Private Methods
        private static void AddArrayMetadata (Type type)
        {
            if (array_metadata.ContainsKey (type))
                return;

            ArrayMetadata data = new ArrayMetadata ();

            data.IsArray = type.IsArray;

            if (type.GetInterface ("System.Collections.IList") != null)
                data.IsList = true;

            foreach (PropertyInfo p_info in type.GetProperties ()) {
                if (p_info.Name != "Item")
                    continue;

                ParameterInfo[] parameters = p_info.GetIndexParameters ();

                if (parameters.Length != 1)
                    continue;

                if (parameters[0].ParameterType == typeof (int))
                    data.ElementType = p_info.PropertyType;
            }

            lock (array_metadata_lock) {
                try {
                    array_metadata.Add (type, data);
                } catch (ArgumentException) {
                    return;
                }
            }
        }

        private static void AddObjectMetadata (Type type)
        {
            if (object_metadata.ContainsKey (type))
                return;

            ObjectMetadata data = new ObjectMetadata ();

            if (type.GetInterface ("System.Collections.IDictionary") != null)
                data.IsDictionary = true;

            data.Properties = new Dictionary<string, PropertyMetadata> ();

            foreach (PropertyInfo p_info in type.GetProperties ()) {
                if (p_info.Name == "Item") {
                    ParameterInfo[] parameters = p_info.GetIndexParameters ();

                    if (parameters.Length != 1)
                        continue;

                    if (parameters[0].ParameterType == typeof (string))
                        data.ElementType = p_info.PropertyType;

                    continue;
                }

                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = p_info;
                p_data.Type = p_info.PropertyType;

                data.Properties.Add (p_info.Name, p_data);
            }

            foreach (FieldInfo f_info in type.GetFields ()) {
                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = f_info;
                p_data.IsField = true;
                p_data.Type = f_info.FieldType;

                data.Properties.Add (f_info.Name, p_data);
            }

            lock (object_metadata_lock) {
                try {
                    object_metadata.Add (type, data);
                } catch (ArgumentException) {
                    return;
                }
            }
        }

        private static void AddTypeProperties (Type type)
        {
            if (type_properties.ContainsKey (type))
                return;

            IList<PropertyMetadata> props = new List<PropertyMetadata> ();

            foreach (PropertyInfo p_info in type.GetProperties ()) {
                if (p_info.Name == "Item")
                    continue;

                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = p_info;
                p_data.IsField = false;
                props.Add (p_data);
            }

            foreach (FieldInfo f_info in type.GetFields ()) {
                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = f_info;
                p_data.IsField = true;

                props.Add (p_data);
            }

            lock (type_properties_lock) {
                try {
                    type_properties.Add (type, props);
                } catch (ArgumentException) {
                    return;
                }
            }
        }

        private static MethodInfo GetConvOp (Type t1, Type t2)
        {
            lock (conv_ops_lock) {
                if (! conv_ops.ContainsKey (t1))
                    conv_ops.Add (t1, new Dictionary<Type, MethodInfo> ());
            }

            if (conv_ops[t1].ContainsKey (t2))
                return conv_ops[t1][t2];

            MethodInfo op = t1.GetMethod (
                "op_Implicit", new Type[] { t2 });

            lock (conv_ops_lock) {
                try {
                    conv_ops[t1].Add (t2, op);
                } catch (ArgumentException) {
                    return conv_ops[t1][t2];
                }
            }

            return op;
        }

        private static object ReadValue (Type inst_type, JsonReader reader)
        {
            reader.Read ();

            if (reader.Token == JsonToken.ArrayEnd)
                return null;

            if (reader.Token == JsonToken.Null) {

                if (! inst_type.IsClass)
                    throw new JsonException (String.Format (
                            "Can't assign null to an instance of type {0}",
                            inst_type));

                return null;
            }

            if (reader.Token == JsonToken.Double ||
                reader.Token == JsonToken.Int ||
                reader.Token == JsonToken.Long ||
                reader.Token == JsonToken.String ||
                reader.Token == JsonToken.Boolean) {

                Type json_type = reader.Value.GetType ();

                if (inst_type.IsAssignableFrom (json_type))
                    return reader.Value;

                MethodInfo conv_op = GetConvOp (inst_type, json_type);

                if (conv_op == null)
                    throw new JsonException (String.Format (
                            "Can't assign value '{0}' (type {1}) to type {2}",
                            reader.Value, json_type, inst_type));

                return conv_op.Invoke (null, new object[] { reader.Value });
            }

            object instance = null;

            if (reader.Token == JsonToken.ArrayStart) {

                AddArrayMetadata (inst_type);
                ArrayMetadata t_data = array_metadata[inst_type];

                if (! t_data.IsArray && ! t_data.IsList)
                    throw new JsonException (String.Format (
                            "Type {0} can't act as an array",
                            inst_type));

                IList list;
                Type elem_type;

                if (! t_data.IsArray) {
                    list = (IList) Activator.CreateInstance (inst_type);
                    elem_type = t_data.ElementType;
                } else {
                    list = new ArrayList ();
                    elem_type = inst_type.GetElementType ();
                }

                while (true) {
                    object item = ReadValue (elem_type, reader);
                    if (reader.Token == JsonToken.ArrayEnd)
                        break;

                    list.Add (item);
                }

                if (t_data.IsArray) {
                    int n = list.Count;
                    instance = Array.CreateInstance (elem_type, n);

                    for (int i = 0; i < n; i++)
                        ((Array) instance).SetValue (list[i], i);
                } else
                    instance = list;

            } else if (reader.Token == JsonToken.ObjectStart) {

                AddObjectMetadata (inst_type);
                ObjectMetadata t_data = object_metadata[inst_type];

                instance = Activator.CreateInstance (inst_type);

                while (true) {
                    reader.Read ();

                    if (reader.Token == JsonToken.ObjectEnd)
                        break;

                    string property = (string) reader.Value;

                    if (t_data.Properties.ContainsKey (property)) {
                        PropertyMetadata prop_data =
                            t_data.Properties[property];

                        if (prop_data.IsField) {
                            ((FieldInfo) prop_data.Info).SetValue (
                                instance, ReadValue (prop_data.Type, reader));
                        } else {
                            ((PropertyInfo) prop_data.Info).SetValue (
                                instance, ReadValue (prop_data.Type, reader),
                                null);
                        }

                    } else {
                        if (! t_data.IsDictionary)
                            throw new JsonException (String.Format (
                                    "The type {0} doesn't have the " +
                                    "property '{1}'", inst_type, property));

                        ((IDictionary) instance).Add (
                            property, ReadValue (
                                t_data.ElementType, reader));
                    }

                }

            }

            return instance;
        }

        private static IJsonWrapper ReadValue (WrapperFactory factory,
                                               JsonReader reader)
        {
            reader.Read ();

            if (reader.Token == JsonToken.ArrayEnd ||
                reader.Token == JsonToken.Null)
                return null;

            IJsonWrapper instance = factory ();

            if (reader.Token == JsonToken.String) {
                instance.SetString ((string) reader.Value);
                return instance;
            }

            if (reader.Token == JsonToken.Double) {
                instance.SetDouble ((double) reader.Value);
                return instance;
            }

            if (reader.Token == JsonToken.Int) {
                instance.SetInt ((int) reader.Value);
                return instance;
            }

            if (reader.Token == JsonToken.Long) {
                instance.SetLong ((long) reader.Value);
                return instance;
            }

            if (reader.Token == JsonToken.Boolean) {
                instance.SetBoolean ((bool) reader.Value);
                return instance;
            }

            if (reader.Token == JsonToken.ArrayStart) {

                while (true) {
                    IJsonWrapper item = ReadValue (factory, reader);
                    if (reader.Token == JsonToken.ArrayEnd)
                        break;

                    ((IList) instance).Add (item);
                }


            } else if (reader.Token == JsonToken.ObjectStart) {

                while (true) {
                    reader.Read ();

                    if (reader.Token == JsonToken.ObjectEnd)
                        break;

                    string property = (string) reader.Value;

                    ((IDictionary) instance)[property] = ReadValue (
                        factory, reader);
                }

            }

            return instance;
        }

        private static void WriteValue (object obj, JsonWriter writer,
                                        bool writer_is_private)
        {
            if (obj == null) {
                writer.Write (null);
                return;
            }

            if (obj is IJsonWrapper) {
                if (writer_is_private)
                    writer.TextWriter.Write (((IJsonWrapper) obj).ToJson ());
                else
                    ((IJsonWrapper) obj).ToJson (writer);

                return;
            }

            if (obj is String) {
                writer.Write ((string) obj);
                return;
            }

            if (obj is Double) {
                writer.Write ((double) obj);
                return;
            }

            if (obj is Int32) {
                writer.Write ((int) obj);
                return;
            }

            if (obj is Boolean) {
                writer.Write ((bool) obj);
                return;
            }

            if (obj is Int64) {
                writer.Write ((long) obj);
                return;
            }

            if (obj is Array) {
                writer.WriteArrayStart ();

                foreach (object elem in (Array) obj)
                    WriteValue (elem, writer, writer_is_private);

                writer.WriteArrayEnd ();

                return;
            }

            if (obj is IList) {
                writer.WriteArrayStart ();
                foreach (object elem in (IList) obj)
                    WriteValue (elem, writer, writer_is_private);
                writer.WriteArrayEnd ();

                return;
            }

            if (obj is IDictionary) {
                writer.WriteObjectStart ();
                foreach (DictionaryEntry entry in (IDictionary) obj) {
                    writer.WritePropertyName ((string) entry.Key);
                    WriteValue (entry.Value, writer, writer_is_private);
                }
                writer.WriteObjectEnd ();

                return;
            }

            // Default case; a regular .Net object
            Type obj_type = obj.GetType ();
            AddTypeProperties (obj_type);
            IList<PropertyMetadata> props = type_properties[obj_type];

            writer.WriteObjectStart ();
            foreach (PropertyMetadata p_data in props) {
                writer.WritePropertyName (p_data.Info.Name);

                if (p_data.IsField)
                    WriteValue (((FieldInfo) p_data.Info).GetValue (obj),
                                writer, writer_is_private);
                else
                    WriteValue (((PropertyInfo) p_data.Info).GetValue (
                            obj, null), writer, writer_is_private);
            }
            writer.WriteObjectEnd ();
        }
        #endregion


        public static string ToJson (object obj)
        {
            StringWriter sw = new StringWriter ();

            JsonWriter writer = new JsonWriter (sw);

            WriteValue (obj, writer, true);

            return sw.ToString ();
        }

        public static void ToJson (object obj, JsonWriter writer)
        {
            WriteValue (obj, writer, false);
        }

        public static JsonData ToObject (JsonReader reader)
        {
            return (JsonData) ToWrapper (
                delegate { return new JsonData (); }, reader);
        }

        public static JsonData ToObject (TextReader reader)
        {
            JsonReader json_reader = new JsonReader (reader);

            return (JsonData) ToWrapper (
                delegate { return new JsonData (); }, json_reader);
        }

        public static JsonData ToObject (string json)
        {
            return (JsonData) ToWrapper (
                delegate { return new JsonData (); }, json);
        }

        public static T ToObject<T> (JsonReader reader)
        {
            return (T) ReadValue (typeof (T), reader);
        }

        public static T ToObject<T> (TextReader reader)
        {
            JsonReader json_reader = new JsonReader (reader);

            return (T) ReadValue (typeof (T), json_reader);
        }

        public static T ToObject<T> (string json)
        {
            JsonReader reader = new JsonReader (json);

            return (T) ReadValue (typeof (T), reader);
        }

        public static IJsonWrapper ToWrapper (WrapperFactory factory,
                                              JsonReader reader)
        {
            return ReadValue (factory, reader);
        }

        public static IJsonWrapper ToWrapper (WrapperFactory factory,
                                              string json)
        {
            JsonReader reader = new JsonReader (json);

            return ReadValue (factory, reader);
        }
    }
}
