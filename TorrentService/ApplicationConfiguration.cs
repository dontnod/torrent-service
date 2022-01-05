// Copyright 2017-2019 Dontnod Entertainment

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization;

namespace Dontnod.TorrentService
{
    [DataContract]
    public class ApplicationConfiguration
    {
        public ApplicationConfiguration()
        {
            Port = 52138;
            DirectoriesToWatch = new List<string>();
        }

        [DataMember]
        public int Port { get; set; }
        [DataMember]
        [JsonConverter(typeof(IPAddressConverter))]
        public IPAddress ReportedAddress { get; set; }
        [DataMember]
        public List<string> DirectoriesToWatch { get; set; }
        [DataMember]
        public string FastResumePath { get; set; }
        [DataMember]
        public string StatusFilePath { get; set; }
    }

    class IPAddressConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(IPAddress));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return IPAddress.Parse((string)reader.Value);
        }
    }
}
