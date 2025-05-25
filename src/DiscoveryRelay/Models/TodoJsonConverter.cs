using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscoveryRelay.Models;

public class TodoJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(Todo) || typeToConvert == typeof(Todo[]);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(Todo))
        {
            return new TodoConverter();
        }
        else if (typeToConvert == typeof(Todo[]))
        {
            return new TodoArrayConverter();
        }

        throw new ArgumentException($"Cannot convert {typeToConvert}");
    }

    private class TodoConverter : JsonConverter<Todo>
    {
        public override Todo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected start object token");
            }

            int id = 0;
            string? title = null;
            DateOnly? dueBy = null;
            bool isComplete = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected property name");
                }

                string? propertyName = reader.GetString();
                reader.Read();

                switch (propertyName?.ToLower())
                {
                    case "id":
                        id = reader.GetInt32();
                        break;
                    case "title":
                        title = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        break;
                    case "dueby":
                        if (reader.TokenType == JsonTokenType.Null)
                        {
                            dueBy = null;
                        }
                        else if (reader.TokenType == JsonTokenType.String)
                        {
                            string? dateStr = reader.GetString();
                            if (dateStr != null && DateOnly.TryParse(dateStr, out var date))
                            {
                                dueBy = date;
                            }
                        }
                        break;
                    case "iscomplete":
                        isComplete = reader.GetBoolean();
                        break;
                }
            }

            return new Todo(id, title, dueBy, isComplete);
        }

        public override void Write(Utf8JsonWriter writer, Todo value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", value.Id);

            writer.WritePropertyName("title");
            if (value.Title == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.Title);

            writer.WritePropertyName("dueBy");
            if (value.DueBy == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.DueBy.Value.ToString("yyyy-MM-dd"));

            writer.WriteBoolean("isComplete", value.IsComplete);
            writer.WriteEndObject();
        }
    }

    private class TodoArrayConverter : JsonConverter<Todo[]>
    {
        public override Todo[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Expected start array token");
            }

            var todoList = new List<Todo>();
            var todoConverter = new TodoConverter();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                todoList.Add(todoConverter.Read(ref reader, typeof(Todo), options));
            }

            return todoList.ToArray();
        }

        public override void Write(Utf8JsonWriter writer, Todo[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            var todoConverter = new TodoConverter();
            foreach (var todo in value)
            {
                todoConverter.Write(writer, todo, options);
            }
            writer.WriteEndArray();
        }
    }
}
