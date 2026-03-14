using System.Text.Json;
using Achates.Providers.Util;

namespace Achates.Tests;

public sealed class JsonSchemaHelpersTests
{
    // --- StringSchema ---

    [Fact]
    public void StringSchema_basic()
    {
        var schema = JsonSchemaHelpers.StringSchema();

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("description", out _));
    }

    [Fact]
    public void StringSchema_with_description()
    {
        var schema = JsonSchemaHelpers.StringSchema("A name");

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("A name", schema.GetProperty("description").GetString());
    }

    // --- NumberSchema ---

    [Fact]
    public void NumberSchema_basic()
    {
        var schema = JsonSchemaHelpers.NumberSchema();

        Assert.Equal("number", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void NumberSchema_with_description()
    {
        var schema = JsonSchemaHelpers.NumberSchema("A count");

        Assert.Equal("A count", schema.GetProperty("description").GetString());
    }

    // --- BooleanSchema ---

    [Fact]
    public void BooleanSchema_basic()
    {
        var schema = JsonSchemaHelpers.BooleanSchema();

        Assert.Equal("boolean", schema.GetProperty("type").GetString());
    }

    // --- StringEnum ---

    [Fact]
    public void StringEnum_with_values()
    {
        var schema = JsonSchemaHelpers.StringEnum(["a", "b", "c"]);

        Assert.Equal("string", schema.GetProperty("type").GetString());
        var enumValues = schema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["a", "b", "c"], enumValues);
    }

    [Fact]
    public void StringEnum_with_description_and_default()
    {
        var schema = JsonSchemaHelpers.StringEnum(["x", "y"], "Pick one", "x");

        Assert.Equal("Pick one", schema.GetProperty("description").GetString());
        Assert.Equal("x", schema.GetProperty("default").GetString());
    }

    [Fact]
    public void StringEnum_without_default_omits_it()
    {
        var schema = JsonSchemaHelpers.StringEnum(["x"]);

        Assert.False(schema.TryGetProperty("default", out _));
    }

    // --- ObjectSchema ---

    [Fact]
    public void ObjectSchema_with_properties()
    {
        var schema = JsonSchemaHelpers.ObjectSchema(
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSchemaHelpers.StringSchema(),
                ["age"] = JsonSchemaHelpers.NumberSchema(),
            },
            required: ["name"]);

        Assert.Equal("object", schema.GetProperty("type").GetString());

        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("age").GetProperty("type").GetString());

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["name"], required);
    }

    [Fact]
    public void ObjectSchema_without_required_omits_it()
    {
        var schema = JsonSchemaHelpers.ObjectSchema(
            new Dictionary<string, JsonElement>
            {
                ["x"] = JsonSchemaHelpers.StringSchema(),
            });

        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Fact]
    public void ObjectSchema_with_description()
    {
        var schema = JsonSchemaHelpers.ObjectSchema(
            new Dictionary<string, JsonElement>(),
            description: "A thing");

        Assert.Equal("A thing", schema.GetProperty("description").GetString());
    }

    [Fact]
    public void ObjectSchema_nested()
    {
        var inner = JsonSchemaHelpers.ObjectSchema(
            new Dictionary<string, JsonElement>
            {
                ["value"] = JsonSchemaHelpers.NumberSchema(),
            });

        var outer = JsonSchemaHelpers.ObjectSchema(
            new Dictionary<string, JsonElement>
            {
                ["nested"] = inner,
            });

        var nestedType = outer.GetProperty("properties")
            .GetProperty("nested")
            .GetProperty("type")
            .GetString();
        Assert.Equal("object", nestedType);
    }
}
