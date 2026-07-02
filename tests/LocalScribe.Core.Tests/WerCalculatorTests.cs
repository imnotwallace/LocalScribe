using LocalScribe.Core.Pipeline;

public class WerCalculatorTests
{
    [Fact]
    public void Identical_text_is_zero_wer()
        => Assert.Equal(0.0, WerCalculator.Wer("the hearing is on Thursday", "The hearing is on Thursday."));

    [Fact]
    public void One_substitution_in_four_words_is_25_percent()
        => Assert.Equal(0.25, WerCalculator.Wer("the hearing on thursday", "the hearing on friday"), 2);

    [Fact]
    public void Empty_hypothesis_is_total_error()
        => Assert.Equal(1.0, WerCalculator.Wer("some reference words", ""));

    [Fact]
    public void Both_empty_is_zero()
        => Assert.Equal(0.0, WerCalculator.Wer("", ""));
}
