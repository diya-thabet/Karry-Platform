using Karry.SharedKernel.Results;

namespace Karry.SharedKernel.UnitTests;

[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-018")]
public sealed class ResultTests
{
    private static readonly Error NotFound = Error.NotFound("asset.not_found", "Asset not found.");

    [Fact]
    public void Success_exposes_the_value()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Reading_the_value_of_a_failure_throws_with_the_error_code()
    {
        Result<int> result = NotFound;

        result.IsSuccess.Should().BeFalse();
        ((Action)(() => _ = result.Value)).Should().Throw<InvalidOperationException>().WithMessage("*asset.not_found*");
    }

    [Fact]
    public void Map_and_bind_short_circuit_on_failure()
    {
        var calls = 0;
        Result<int> failure = NotFound;

        var mapped = failure.Map(v => { calls++; return v * 2; }).Bind(v => { calls++; return Result.Success(v + 1); });

        calls.Should().Be(0);
        mapped.Error.Should().Be(NotFound);
    }

    [Fact]
    public void Map_and_bind_compose_on_success()
    {
        var result = Result.Success(20).Map(v => v * 2).Bind(v => v > 0 ? Result.Success(v + 2) : Result.Failure<int>(NotFound));

        result.Value.Should().Be(42);
    }

    [Fact]
    public void Errors_require_a_code_and_a_message()
    {
        ((Action)(() => Error.Conflict("", "x"))).Should().Throw<ArgumentException>();
        ((Action)(() => Error.Conflict("x", " "))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validation_errors_carry_field_messages()
    {
        var error = Error.Validation("asset.invalid", "Invalid asset.", new Dictionary<string, string[]> { ["code"] = ["Required."] });

        error.Kind.Should().Be(ErrorKind.Validation);
        error.FieldErrors["code"].Should().Equal("Required.");
    }
}
