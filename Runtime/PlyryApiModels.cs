using System;
using System.Collections.Generic;
using System.Text;

namespace Plyry.Unity
{
    [Serializable]
    public sealed class PlyryApiResponse
    {
        public bool ok;
        public string error;
        public string message;
        public string question;
        public string signup_challenge;
        public string access_token;
        public string refresh_token;
        public string token_type;
        public int expires_in;
        public bool created;
        public PlyryLinkedApp linked_app;
        public PlyryWallet wallet;
        public int wallet_cents;
        public int price_cents;
        public bool has_sufficient_funds;
        public PlyryAccount user;
        public PlyryJob job;
        public PlyryPaymentConfig payment_config;
        public PlyryTopupPayment payment;
        public string request_status;
    }

    [Serializable]
    public sealed class PlyryLinkedApp
    {
        public int id;
        public string name;
        public string origin;
    }

    [Serializable]
    public sealed class PlyryWallet
    {
        public int wallet_cents;
        public string currency;
        public int minimum_topup_cents;
        public int maximum_topup_cents;
    }

    [Serializable]
    public sealed class PlyryPaymentConfig
    {
        public bool ok;
        public string driver;
        public PlyryOnvoConfig onvo;
        public string return_url;
    }

    [Serializable]
    public sealed class PlyryOnvoConfig
    {
        public string api_base;
        public string publishable_key;
    }

    [Serializable]
    public sealed class PlyryOnvoPaymentMethod
    {
        public string id;
        public string message;
    }

    [Serializable]
    public sealed class PlyryTopupPayment
    {
        public string reference;
        public string status;
        public int amount_cents;
        public bool requires_action;
        public string action_url;
    }

    [Serializable]
    public sealed class PlyryAccount
    {
        public int id;
        public string email;
        public string role;
        public int wallet_cents;
        public bool is_active;
    }

    [Serializable]
    public sealed class PlyryJob
    {
        public string hash;
        public string status;
        public string type;
        public string error;
        public int price_cents;
        public bool media_available;
        public string media_url;
        public string scene_url;
        public string mime;
    }

    [Serializable]
    public sealed class PlyryCatalog
    {
        public bool ok;
        public int api_version;
        public string[] media_types;
        public int minimum_funds_cents;
        public int maximum_funds_cents;
        public PlyryMediaCatalogEntry[] models;

        public PlyryMediaCatalogEntry FindType(string type)
        {
            if (models == null) return null;
            for (var i = 0; i < models.Length; i++)
                if (string.Equals(models[i].type, type, StringComparison.OrdinalIgnoreCase))
                    return models[i];
            return null;
        }
    }

    [Serializable]
    public sealed class PlyryMediaCatalogEntry
    {
        public string type;
        public string default_model;
        public PlyryModelOption[] options;
    }

    [Serializable]
    public sealed class PlyryModelOption
    {
        public string id;
        public string label;
        public string id_label;
        public int price_cents;
        public int test_price_cents;
        public bool requires_reference;
        public int prompt_min;
        public int prompt_max;
        public PlyryGenerationOptions generation_options;
        public PlyryReferenceMode[] references;
    }

    [Serializable]
    public sealed class PlyryGenerationOptions
    {
        public PlyryIntegerSetting duration;
        public PlyryChoiceSetting aspect;
        public PlyryChoiceSetting resolution;
        public PlyryBooleanSetting audio;
        public PlyryRangeSetting polygon_count;
    }

    [Serializable]
    public sealed class PlyryIntegerSetting
    {
        public string key;
        public int[] values;
        public int @default;
        public int default_test;
    }

    [Serializable]
    public sealed class PlyryChoiceSetting
    {
        public string key;
        public bool emit = true;
        public PlyryChoice[] values;
        public string @default;
        public string default_test;
        public string default_full;
        public string label;
    }

    [Serializable]
    public sealed class PlyryChoice
    {
        public string value;
        public string label;
    }

    [Serializable]
    public sealed class PlyryBooleanSetting
    {
        public string key;
        public bool @default;
        public bool default_test;
    }

    [Serializable]
    public sealed class PlyryRangeSetting
    {
        public string key;
        public int min;
        public int max;
        public int step;
        public int @default;
    }

    [Serializable]
    public sealed class PlyryReferenceMode
    {
        public string mode;
        public string model;
        public string payload;
        public int max_files;
        public string[] accept;
        public string[] require_any_media_types;
        public string[] require_all_media_types;
        public PlyryReferenceField[] fields;
        public PlyryGenerationOptions generation_options;
    }

    [Serializable]
    public sealed class PlyryReferenceField
    {
        public string field;
        public string role;
        public string label;
        public string media_type;
    }

    public sealed class PlyryReference
    {
        public string FileName;
        public string MimeType;
        public int Slot;
        public byte[] Data;

        public PlyryReference(string fileName, string mimeType, int slot, byte[] data)
        {
            FileName = fileName;
            MimeType = mimeType;
            Slot = slot;
            Data = data;
        }
    }

    public sealed class PlyryDownloadedMedia
    {
        public string TemporaryPath;
        public string Extension;
        public string MimeType;
        public string Sha256;
        public string CacheToken;
    }

    [Serializable]
    public sealed class PlyrySettingsSelection
    {
        public int duration;
        public string aspect;
        public string resolution;
        public bool audio;
        public int polygonCount;

        public static PlyrySettingsSelection Defaults(PlyryGenerationOptions options, bool testQuality)
        {
            var value = new PlyrySettingsSelection();
            value.Reset(options, testQuality);
            return value;
        }

        public void Reset(PlyryGenerationOptions options, bool testQuality)
        {
            if (options == null) return;
            if (options.duration != null)
                duration = testQuality && options.duration.default_test > 0
                    ? options.duration.default_test
                    : options.duration.@default;

            if (options.aspect != null)
                aspect = testQuality && !string.IsNullOrEmpty(options.aspect.default_test)
                    ? options.aspect.default_test
                    : options.aspect.@default;

            if (options.resolution != null)
            {
                var selected = testQuality && !string.IsNullOrEmpty(options.resolution.default_test)
                    ? options.resolution.default_test
                    : (!testQuality && !string.IsNullOrEmpty(options.resolution.default_full)
                        ? options.resolution.default_full
                        : options.resolution.@default);
                resolution = selected;
            }

            if (options.audio != null)
                audio = testQuality ? options.audio.default_test : options.audio.@default;

            if (options.polygon_count != null)
                polygonCount = options.polygon_count.@default;
        }

        public string ToQueueOptionsJson(PlyryGenerationOptions options)
        {
            var fields = new List<string>();
            if (options != null)
            {
                if (options.duration != null && duration > 0)
                    fields.Add("\"duration\":" + duration);
                if (options.aspect != null && options.aspect.emit && !string.IsNullOrEmpty(aspect))
                    fields.Add("\"aspect\":\"" + Escape(aspect) + "\"");
                if (options.resolution != null && !string.IsNullOrEmpty(resolution))
                    fields.Add("\"resolution\":\"" + Escape(resolution) + "\"");
                if (options.audio != null)
                    fields.Add("\"audio\":" + (audio ? "true" : "false"));
                if (options.polygon_count != null && polygonCount > 0)
                    fields.Add("\"polygon_count\":" + polygonCount);
            }
            return "{\"generation\":{" + string.Join(",", fields.ToArray()) + "}}";
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20) builder.Append("\\u00").Append(((int)character).ToString("x2"));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.ToString();
        }
    }
}





