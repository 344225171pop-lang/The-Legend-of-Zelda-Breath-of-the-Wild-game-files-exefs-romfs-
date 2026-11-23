using BotwExtractor.Ui;
using Xunit;

namespace BotwExtractor.Tests
{
    public class ErrorMessagesTests
    {
        const string TitleKeyMessage =
            "Missing NCA title key.\nKey Type: Title\nKey Name: 01007EF00011E0000000000000000000";

        [Fact]
        public void TitleKeyMissing_GivesActionableTitleKeysGuidance()
        {
            string text = ErrorMessages.Describe("MissingKeyException", TitleKeyMessage);
            Assert.Contains("title key", text);
            Assert.Contains("title.keys", text);
            Assert.Contains("01007EF00011E0000000000000000000", text);
            // Concise: no stack-trace frames leak into the user-facing text.
            Assert.DoesNotContain("at LibHac", text);
            Assert.DoesNotContain("at Extractor", text);
        }

        [Fact]
        public void TitleKeyMissing_WithoutRightsId_StillGuides()
        {
            string text = ErrorMessages.Describe("MissingKeyException", "Missing NCA title key.");
            Assert.Contains("title.keys", text);
            Assert.DoesNotContain("Rights ID", text);
        }

        [Fact]
        public void KeyAreaMissing_PointsAtProdKeys()
        {
            string text = ErrorMessages.Describe("MissingKeyException", "Missing key area key.");
            Assert.Contains("prod.keys", text);
            Assert.DoesNotContain("title.keys", text);
        }

        [Fact]
        public void HeaderKeyMissing_PointsAtProdKeys()
        {
            string text = ErrorMessages.Describe("MissingKeyException", "Missing header key.");
            Assert.Contains("prod.keys", text);
        }

        [Fact]
        public void GenericException_ReturnsConciseMessage()
        {
            string text = ErrorMessages.Describe("IOException", "Disk full while writing output.");
            Assert.Equal("Disk full while writing output.", text);
        }

        [Fact]
        public void NullOrEmptyMessage_FallsBackSafely()
        {
            Assert.False(string.IsNullOrWhiteSpace(ErrorMessages.Describe("Exception", null)));
            Assert.False(string.IsNullOrWhiteSpace(ErrorMessages.Describe("Exception", "")));
        }
    }
}