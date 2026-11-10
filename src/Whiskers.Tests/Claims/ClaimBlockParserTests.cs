namespace Whiskers.Tests.Claims;

/// <summary>Contract 0011, D10/D11: how a governed document is cut into blocks and what makes a block claim-shaped.</summary>
public class ClaimBlockParserTests
{
    private static IReadOnlyList<ClaimBlock> Parse(params string[] lines) => ClaimBlockParser.Parse(string.Join("\n", lines));

    [Fact]
    public void Paragraphs_end_at_a_blank_line_and_headings_are_no_blocks()
    {
        var blocks = Parse("# Title", "", "First line", "second line <!-- claim:RM-01 -->", "", "## Next", "Another one.");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("First line\nsecond line <!-- claim:RM-01 -->", blocks[0].Text);
        Assert.Equal(3, blocks[0].Line);
        Assert.Equal(["RM-01"], blocks[0].ClaimIds);
        Assert.Equal("First line second line", blocks[0].Normalized);
        Assert.False(blocks[1].IsMarked);
    }

    [Fact]
    public void A_list_item_keeps_its_continuation_lines_and_a_nested_item_is_its_own_block()
    {
        var blocks = Parse("- outer item", "  continued <!-- claim-free:advice -->", "  - nested item <!-- claim:SEC-02 -->", "- next item");
        Assert.Equal(["- outer item\n  continued <!-- claim-free:advice -->", "  - nested item <!-- claim:SEC-02 -->", "- next item"],
            blocks.Select(b => b.Text));
        Assert.Equal("advice", blocks[0].FreeReason);
        Assert.Equal(["SEC-02"], blocks[1].ClaimIds);
        Assert.All(blocks, b => Assert.Equal("item", b.Kind));
    }

    [Fact]
    public void A_table_body_row_is_a_block_its_header_is_not_and_a_marker_may_sit_in_a_cell()
    {
        var blocks = Parse("| Name | Value |", "|---|---|", "| a | 1 <!-- claim:PC-01,PC-02 --> |", "| b | 2 |");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(["PC-01", "PC-02"], blocks[0].ClaimIds);
        Assert.Equal("| a | 1 |", blocks[0].Normalized);
        Assert.All(blocks, b => Assert.Equal("row", b.Kind));
    }

    [Fact]
    public void A_fenced_block_is_one_block_and_takes_the_marker_line_after_its_closing_fence()
    {
        var blocks = Parse("```bash", "echo 1", "", "# not a heading", "```", "<!-- claim-free:advice -->", "", "After.");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("fence", blocks[0].Kind);
        Assert.Equal("advice", blocks[0].FreeReason);
        Assert.Contains("# not a heading", blocks[0].Text);
        Assert.Equal("After.", blocks[1].Text);
    }

    [Fact]
    public void Lines_that_hold_only_links_or_images_and_lone_comments_are_no_blocks()
    {
        var blocks = Parse("[![Badge](https://x/b.svg)](https://x)", "[One](a.md) · [Two](b.md)", "", "<!-- a note -->", "",
            "Text with a [link](https://example.org/v1.2).");
        Assert.Equal(["Text with a [link](https://example.org/v1.2)."], blocks.Select(b => b.Text));
        Assert.False(blocks[0].IsClaimShaped);   // the link's destination holds the digits, not the text
    }

    [Fact]
    public void A_block_is_claim_shaped_by_a_backtick_a_digit_or_a_trigger_word()
    {
        Assert.True(ClaimRules.IsClaimShaped("Set `X`."));
        Assert.True(ClaimRules.IsClaimShaped("Within 10 minutes."));
        Assert.True(ClaimRules.IsClaimShaped("Keys are Deleted on start."));
        Assert.True(ClaimRules.IsClaimShaped("compared in constant time"));
        Assert.True(ClaimRules.IsClaimShaped("Nothing else."));                 // "no…"
        Assert.False(ClaimRules.IsClaimShaped("A design for the readers."));    // "sign" inside a word is no trigger
        Assert.False(ClaimRules.IsClaimShaped("Plain words here."));
    }

    [Fact]
    public void The_fingerprint_ignores_markers_and_whitespace_but_not_words()
    {
        var a = ClaimRules.Fingerprint("A  claim\nhere <!-- claim:RM-01 -->");
        Assert.Equal(a, ClaimRules.Fingerprint("A claim here"));
        Assert.NotEqual(a, ClaimRules.Fingerprint("A claim here, and more."));
        Assert.Equal(12, a.Length);
    }
}
