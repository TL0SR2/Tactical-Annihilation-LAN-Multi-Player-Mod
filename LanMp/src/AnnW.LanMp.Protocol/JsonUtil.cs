using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AnnW.LanMp.Protocol
{
    public static class JsonUtil
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new PublicFieldsContractResolver(),
            NullValueHandling = NullValueHandling.Include
        };

        public static string ToJson(object obj) => JsonConvert.SerializeObject(obj, Settings);

        public static T FromJson<T>(string json)
        {
            if (string.IsNullOrEmpty(json))
                return default;
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        private sealed class PublicFieldsContractResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                var props = new List<JsonProperty>();
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var prop = base.CreateProperty(field, memberSerialization);
                    prop.Writable = true;
                    prop.Readable = true;
                    props.Add(prop);
                }

                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead)
                        continue;
                    var prop = base.CreateProperty(property, memberSerialization);
                    prop.Writable = property.CanWrite;
                    prop.Readable = true;
                    props.Add(prop);
                }

                return props;
            }
        }
    }
}
