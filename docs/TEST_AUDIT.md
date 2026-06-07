# WinNewsWire Unit Test Audit — Detailed Specification

Companion to `PORT_SPEC.md` and `PORT_STATUS.md`.  
Covers a line-by-line comparison of every test in the original **NetNewsWire** (Swift)
repository against the **WinNewsWire** (C#/.NET) port.

---

## Table of contents

1. [Methodology](#1-methodology)
2. [Test inventory matrix](#2-test-inventory-matrix)
3. [Ported tests with logic differences](#3-ported-tests-with-logic-differences)
4. [Missing test suites](#4-missing-test-suites)
5. [New tests unique to WinNewsWire](#5-new-tests-unique-to-winnewswire)
6. [Recommended new tests for both codebases](#6-recommended-new-tests-for-both-codebases)
7. [Remediation plan](#7-remediation-plan)

---

## 1. Methodology

Every Swift test file was read from the NetNewsWire repository at
`C:\Users\qinjo\source\repos\NetNewsWire` and compared against the corresponding C#
test file in `C:\Users\qinjo\source\repos\WinNewsWire`.

Comparison was performed at three levels:

1. **Structural** — does a corresponding test class/method exist?
2. **Semantic** — does the C# test exercise the same assertion logic?
3. **Coverage** — are there behaviours in the C# codebase that have no corresponding
   test in either repository?

Performance tests (`self.measure { … }`) in the original are intentionally excluded
from the parity comparison. These are XCTest-specific benchmarks that map to
BenchmarkDotNet in .NET and are not required for correctness.

Placeholder/example tests in the original (`testExample` stubs in CloudKitSync,
FeedFinder, NewsBlur, RSDatabase) are excluded — they assert nothing and would not
provide value if ported.

---

## 2. Test inventory matrix

### 2.1  Parser tests — RSParser → Parsers.Tests

| # | Swift test method (RSParser) | C# test method (Parsers.Tests) | Status |
|---|---|---|---|
| | **AtomParserTests.swift** | **AtomParserTests.cs** | |
| 1 | `testDaringFireballPerformance` | — | ⏭ perf, skip |
| 2 | `testAllThisPerformance` | — | ⏭ perf, skip |
| 3 | `testGettingHomePageLink` | `HomePageLinks` | ✅ equivalent |
| 4 | `testArticlePermalinks` | `ArticlePermalinks` | ✅ equivalent |
| 5 | `testArticleExternalLinks` | `DaringFireballExternalLinks` | ✅ equivalent |
| 6 | `testDaringFireball` | `DaringFireballItems` | ✅ equivalent |
| 7 | `test4fsodonlineAttachments` | `FourFsodonlineAttachments` | ✅ equivalent |
| 8 | `testExpertOpinionENTAttachments` | `ExpertOpinionEntAttachments` | ✅ equivalent |
| 9 | `testFeedIconURL` | `RootAuthorFeedIconUrl` | ✅ equivalent |
| 10 | `testAuthorAtRoot` | `AuthorAtRoot` | ✅ equivalent |
| | **RSSParserTests.swift** | **RssParserTests.cs** | |
| 11 | `testScriptingNewsPerformance` | — | ⏭ perf, skip |
| 12 | `testKatieFloydPerformance` | — | ⏭ perf, skip |
| 13 | `testEMarleyPerformance` | — | ⏭ perf, skip |
| 14 | `testMantonPerformance` | — | ⏭ perf, skip |
| 15 | `testNatashaTheRobot` | `NatashaTheRobotItemCount` | ✅ equivalent |
| 16 | `testTheOmniShowAttachments` | `TheOmniShowAttachments` | ✅ equivalent |
| 17 | `testTheOmniShowUniqueIDs` | `TheOmniShowUniqueIds` | ✅ equivalent |
| 18 | `testMacworldUniqueIDs` | `MacworldUniqueIdsAreMd5` | ✅ equivalent |
| 19 | `testMacworldAuthors` | `MacworldAuthorsHaveNamesOnly` | ✅ equivalent |
| 20 | `testMonkeyDomGuids` | `MonkeydomGuidsThatArentPermalinks` | ✅ equivalent |
| 21 | `testEmptyContentEncoded` | `EmptyContentEncodedIsIgnored` | ✅ equivalent |
| 22 | `testFeedKnownToHaveGuidsThatArentPermalinks` | `LivemintGuidsNotPermalinks` | ✅ equivalent |
| 23 | `testAuthorsWithTitlesInside` | `CloudblogAuthorTitleNotUsedAsItemTitle` | ✅ equivalent |
| 24 | `testTitlesWithInvalidFeedWithImageStructures` | `AktualityTitlesPresent` | ✅ equivalent |
| 25 | `testFeedLanguage` | `MantonFeedLanguage` | ✅ equivalent |
| 26 | `testFeedIconURL` | `KatieFloydIconUrl` | ✅ equivalent |
| 27 | `testFeedIconURLNotSetByItemLevelImages` | `AktualityIconNotSetFromItemImages` | ✅ equivalent |
| 28 | `testMedscapeExternalURLs` | `MedscapeExternalUrls` | ✅ equivalent |
| 29 | `testMarkdown1` | — | ❌ **MISSING** |
| 30 | `testMarkdown2` | — | ❌ **MISSING** |
| | **JSONFeedParserTests.swift** | **JsonFeedParserTests.cs** | |
| 31 | `testInessentialPerformance` | — | ⏭ perf, skip |
| 32 | `testDaringFireballPerformance` | — | ⏭ perf, skip |
| 33 | `testGettingFaviconAndIconURLs` | `DaringFireballIconUrls` | ✅ equivalent |
| 34 | `testAllThis` | `AllThisItemCount` | ✅ equivalent |
| 35 | `testCurt` | `CurtContainsTwitterQuitter` | ✅ equivalent |
| 36 | `testPixelEnvy` | `PxlnvItemCount` | ✅ equivalent |
| 37 | `testRose` | `RoseItemCount` | ✅ equivalent |
| 38 | `test3960` | `ThreeSixtyLanguage` | ✅ equivalent |
| 39 | `testAuthors` | `AuthorsResolution` | ✅ equivalent |
| | **HTMLMetadataTests.swift** | **HtmlMetadataTests.cs** | |
| 40 | `testDaringFireball` | `DaringFireball` | ✅ equivalent |
| 41 | `testDaringFireballPerformance` | — | ⏭ perf, skip |
| 42 | `testFurbo` | `Furbo` | ✅ equivalent |
| 43 | `testFurboPerformance` | — | ⏭ perf, skip |
| 44 | `testInessential` | `Inessential` | ✅ equivalent |
| 45 | `testInessentialPerformance` | — | ⏭ perf, skip |
| 46 | `testCocoPerformance` | — | ⏭ perf, skip |
| 47 | `testSixColors` | `SixColors` | ✅ equivalent |
| 48 | `testSixColorsPerformance` | — | ⏭ perf, skip |
| 49 | `testCocoOGImage` | `CocoOpenGraphImage` | ✅ equivalent |
| 50 | `testCocoTwitterImage` | `CocoTwitterImage` | ✅ equivalent |
| 51 | `testYouTube` | `YouTubeFeedLinkInBody` | ✅ equivalent |
| | **HTMLLinkTests.swift** | **HtmlLinkTests.cs** | |
| 52 | `testSixColorsPerformance` | — | ⏭ perf, skip |
| 53 | `testSixColorsLink` | `SixColorsHasExpectedLink` | ⚠️ **PARTIAL** — see §3.2 |
| | **FeedParserTypeTests.swift** | **FeedParserTypeTests.cs** | |
| 54 | `testDaringFireballHTMLType` | `HtmlIsNotAFeed("DaringFireball")` | ✅ equivalent |
| 55 | `testFurboHTMLType` | `HtmlIsNotAFeed("furbo")` | ✅ equivalent |
| 56 | `testInessentialHTMLType` | `HtmlIsNotAFeed("inessential")` | ✅ equivalent |
| 57 | `testSixColorsHTMLType` | `HtmlIsNotAFeed("sixcolors")` | ✅ equivalent |
| 58 | `testEMarleyRSSType` | `DetectsRss("EMarley")` | ✅ equivalent |
| 59 | `testScriptingNewsRSSType` | `DetectsRss("scriptingNews")` | ✅ equivalent |
| 60 | `testKatieFloydRSSType` | `DetectsRss("KatieFloyd")` | ✅ equivalent |
| 61 | `testMantonRSSType` | `DetectsRss("manton")` | ✅ equivalent |
| 62 | `testDCRainmakerRSSType` | `DetectsRss("dcrainmaker")` | ✅ equivalent |
| 63 | `testMacworldRSSType` | `DetectsRss("macworld")` | ✅ equivalent |
| 64 | `testNatashaTheRobotRSSType` | `DetectsRss("natasha")` | ✅ equivalent |
| 65 | `testDontHitSaveRSSWithBOMType` | `DetectsRss("donthitsave")` | ✅ equivalent |
| 66 | `testBioRDF` | `DetectsRss("bio")` | ✅ equivalent |
| 67 | `testPHPXML` | `DetectsRss("phpxml")` | ✅ equivalent |
| 68 | `testDaringFireballAtomType` | `DetectsAtom("DaringFireball")` | ✅ equivalent |
| 69 | `testOneFootTsunamiAtomType` | `DetectsAtom("OneFootTsunami")` | ✅ equivalent |
| 70 | `testRussCoxAtomType` | `DetectsAtom("russcox")` | ✅ equivalent |
| 71 | `testScriptingNewsJSONType` | `DetectsRssInJson` | ✅ equivalent |
| 72 | `testInessentialJSONFeedType` | `DetectsJsonFeed("inessential")` | ✅ equivalent |
| 73 | `testAllThisJSONFeedType` | `DetectsJsonFeed("allthis")` | ✅ equivalent |
| 74 | `testCurtJSONFeedType` | `DetectsJsonFeed("curt")` | ✅ equivalent |
| 75 | `testPixelEnvyJSONFeedType` | `DetectsJsonFeed("pxlnv")` | ✅ equivalent |
| 76 | `testRoseJSONFeedType` | `DetectsJsonFeed("rose")` | ✅ equivalent |
| 77 | `testPartialAllThisUnknownFeedType` | `PartialAllThisIsUnknown` | ✅ equivalent |
| 78–81 | 4× performance tests | — | ⏭ perf, skip |
| | **RSDateParserTests.swift** | **DateParserTests.cs** | |
| 82 | `testDateWithString` (17 inline cases) | `ParsesFeedDates` (17 Theory cases) | ✅ equivalent |
| 83 | `testAtomDateWithMissingTCharacter` | `AtomDateWithMissingTCharacter` | ✅ equivalent |
| 84 | `testFeedbinDate` | `FeedbinDate` | ✅ equivalent |
| 85 | `testTwoDigitYear` (4 inline cases) | `TwoDigitYear` (4 Theory cases) | ✅ equivalent |
| 86 | `testHighMillisecondDate` | `HighMillisecondDate` | ✅ equivalent |
| | **OPMLTests.swift** | **OpmlTests.cs** | |
| 87 | `testOPMLParsingPerformance` | — | ⏭ perf, skip |
| 88 | `testNotOPML` | — | ❌ **MISSING** |
| 89 | `testSubsStructure` | `SubsStructure` | ✅ equivalent |
| 90 | `testFindingTitles` | `FindingTitlesWithoutTitleAttribute` | ✅ equivalent |
| | **RSSInJSONParserTests.swift** | **RssInJsonParserTests.cs** | |
| 91 | `testScriptingNewsPerformance` | — | ⏭ perf, skip |
| 92 | `testFeedLanguage` | `ScriptingNewsLanguage` | ✅ equivalent |
| | **EntityDecodingTests.swift** | — | |
| 93 | `test39Decoding` | — | ❌ **MISSING** (entire file) |
| 94 | `testEntities` | — | ❌ **MISSING** (entire file) |

### 2.2  App-level tests — Tests/NetNewsWireTests → Tests/AppShared.Tests

| # | Swift test method | C# test method | Status |
|---|---|---|---|
| | **ArticleSorterTests.swift** | **ArticleSorterTests.cs** | |
| 95 | `testSortByDateAscending` | `SortByDateAscending` | ✅ equivalent |
| 96 | `testSortByDateAscendingWithSameDate` | `SortByDateAscendingWithSameDate` | ✅ equivalent |
| 97 | `testSortByDateAscendingWithGroupByFeed` | — | ❌ **MISSING** |
| 98 | `testSortByDateDescending` | `SortByDateDescending` | ✅ equivalent |
| 99 | `testSortByDateDescendingWithSameDate` | `SortByDateDescendingWithSameDate` | ✅ equivalent |
| 100 | `testSortByDateDescendingWithGroupByFeed` | — | ❌ **MISSING** |
| 101 | `testGroupByFeedWithCaseInsensitiveFeedNames` | `GroupByFeedWithCaseInsensitiveFeedNames` | ✅ equivalent |
| 102 | `testGroupByFeedWithSameFeedNames` | `GroupByFeedWithSameFeedNamesSortsByFeedId` | ✅ equivalent |
| | **SharingTests.swift** | — | |
| 103 | `testSharingSubject` | — | ⛔ macOS-specific (NSSharingService) |
| 104 | `testSharingSubjectMultipleArticles` | — | ⛔ macOS-specific (NSSharingService) |
| | **ScriptingTests/** | — | |
| 105–110 | 6 AppleScript tests | — | ⛔ macOS-specific (AppleScript) |

### 2.3  Core tests — RSCore → (no dedicated test project)

| # | Swift test method | C# equivalent | Status |
|---|---|---|---|
| | **StripHTMLTests.swift** (8 functional tests) | — | ❌ **MISSING** (entire file) |
| 111 | `testStrippingHTMLBasic` | — | ❌ |
| 112 | `testStrippingHTMLWithScript` | — | ❌ |
| 113 | `testStrippingHTMLWithStyle` | — | ❌ |
| 114 | `testStrippingHTMLWithMaxCharacters` | — | ❌ |
| 115 | `testStrippingHTMLWithUTF8` | — | ❌ |
| 116 | `testStrippingHTMLWhitespaceCollapsing` | — | ❌ |
| 117 | `testStrippingHTMLWithRealWorldHTML` | — | ❌ |
| 118 | `testStrippingHTMLMatchesExpectedOutput` | — | ❌ |
| | **MacroProcessorTests.swift** (3 tests) | — | ❌ **MISSING** (entire file) |
| 119 | `testMacroProcessor` | — | ❌ |
| 120 | `testEmptyDelimiters` | — | ❌ |
| 121 | `testMacroInSubstitutions` | — | ❌ |
| | **MainThreadOperationTests.swift** (8 tests) | — | ❌ **MISSING** (entire file) |
| 122 | `testSingleOperation` | — | ❌ |
| 123 | `testOperationAndDependency` | — | ❌ |
| 124 | `testOperationAndDependencyAddedOutOfOrder` | — | ❌ |
| 125 | `testOperationAndTwoDependenciesAddedOutOfOrder` | — | ❌ |
| 126 | `testChildOperationWithTwoDependencies` | — | ❌ |
| 127 | `testAddingManyOperations` | — | ❌ |
| 128 | `testAddingManyOperationsAndCancelingManyOperations` | — | ❌ |
| 129 | `testAddingManyOperationsWithCompletionBlocks` | — | ❌ |
| 130 | `testCancellingOperationsWithName` | — | ❌ |
| | **StringRSCoreTests.swift** (7 tests) | — | ❌ **MISSING** (entire file) |
| 131 | `testCollapsingWhitespace` | — | ❌ |
| 132 | `testTrimmingWhitespace` | — | ❌ |
| 133 | `testStrippingPrefix` | — | ❌ |
| 134 | `testStrippingSuffix` | — | ❌ |
| 135 | `testEscapingSpecialXMLCharacters` | — | ❌ |
| 136 | `testStrippingHTTPOrHTTPSScheme` | — | ❌ |
| 137 | `testNormalizedURL` | — | ❌ |
| | **Data+RSCoreTests.swift** | — | ⏭ Commented out in original |

### 2.4  Web tests — RSWeb → Web.Tests

| # | Swift test method | C# equivalent | Status |
|---|---|---|---|
| | **DictionaryTests.swift** (4 tests) | — | ❌ **MISSING** (entire file) |
| 138 | `testSimpleQueryString` | — | ❌ |
| 139 | `testQueryStringWithAmpersand` | — | ❌ |
| 140 | `testQueryStringWithAccentedCharacters` | — | ❌ |
| 141 | `testQueryStringWithEmoji` | — | ❌ |
| | **StringTests.swift** (1 test) | — | ❌ **MISSING** (entire file) |
| 142 | `testHTMLEscaping` | — | ❌ |

### 2.5  Markdown tests — RSMarkdown → (module empty)

| # | Swift test method | C# equivalent | Status |
|---|---|---|---|
| | **RSMarkdownTests.swift** (~15 tests) | — | ❌ **MISSING** (module not yet ported) |
| 143 | `testBoldFormatting` | — | ❌ |
| 144 | `testHeadingFormatting` | — | ❌ |
| 145 | `testLinkFormatting` | — | ❌ |
| 146 | `testMarkdownParsingPerformance` | — | ⏭ perf, skip |
| 147 | `testInlineCode` | — | ❌ |
| 148 | `testCodeBlocks` | — | ❌ |
| 149 | `testUnorderedList` | — | ❌ |
| 150 | `testOrderedList` | — | ❌ |
| 151 | `testNestedFormatting` | — | ❌ |
| 152 | `testLineBreaksAndParagraphs` | — | ❌ |
| 153 | `testSpecialCharactersAndEscaping` | — | ❌ |
| 154 | `testEmptyString` | — | ❌ |
| 155 | `testPlainText` | — | ❌ |
| 156 | `testMalformedMarkdown` | — | ❌ |
| 157 | `testVeryLargeContent` | — | ❌ |
| 158 | `testMixedMarkdownAndHTML` | — | ❌ |
| 159 | `testUnicodeAndEmoji` | — | ❌ |
| 160 | `testComplexNestedStructures` | — | ❌ |
| 161 | `testRepeatedParsing` | — | ❌ |
| 162 | `testMemoryUsageWithLargeFile` | — | ❌ |

### 2.6  ErrorLog tests

| # | Swift test method | C# equivalent | Status |
|---|---|---|---|
| | **ErrorLogDatabaseTests.swift** (3 tests) | — | ❌ **MISSING** (entire file) |
| 163 | `addAndRetrieveEntry` | — | ❌ |
| 164 | `entriesReturnedInInsertionOrder` | — | ❌ |
| 165 | `pruneOnInit` | — | ❌ |

### 2.7  Account tests

| # | Swift test suite | C# equivalent | Status |
|---|---|---|---|
| | **AccountCredentialsTest.swift** | — | ❌ **MISSING** |
| 166 | `testCreateRetrieveDelete` | — | ❌ |
| | **AccountSettingsImporterTests.swift** (10 tests) | — | ❌ **MISSING** |
| 167–176 | 10 settings import tests | — | ❌ |
| | **FeedSettingsImporterTests.swift** (19 tests) | — | ❌ **MISSING** |
| 177–195 | 19 feed-settings import tests | — | ❌ |
| | **Feedbin/ (3 files)** | — | |
| 196 | `AccountFeedbinSyncTest.testDownloadSync` | — | ❌ **MISSING** |
| 197 | `AccountFeedbinFolderSyncTest.testDownloadSync` | — | ❌ **MISSING** |
| 198 | `AccountFeedbinFolderContentsSyncTest.testDownloadSync` | — | ❌ **MISSING** |
| | **Feedly/ (17 test files, ~50+ tests)** | **FeedlyTests.cs (11 tests)** | |
| 199 | `FeedlyResourceIdTests.testFeedResourceId` | `ResourceIds_FeedUrlRoundTrip` | ✅ equivalent |
| 200 | `FeedlyCheckpointOperationTests` (2 tests) | — | ❌ **MISSING** |
| 201 | `FeedlyCollectionParserTests` (2 tests) | `Collection_DeserializesWithFeeds` | ⚠️ partial — parsing only, no sanitization test |
| 202 | `FeedlyCreateFeedsForCollectionFoldersOperationTests` (2 tests) | — | ❌ **MISSING** |
| 203 | `FeedlyEntryParserTests` (~5 tests) | `Entry_ExternalUrl_PrefersHtmlLinks`, `Entry_DatePublished_DecodesMilliseconds` | ⚠️ partial — URL + date only, no content/summary |
| 204 | `FeedlyFeedParserTests` (2 tests) | — | ❌ **MISSING** |
| 205 | `FeedlyGetCollectionsOperationTests` (2 tests) | — | ❌ **MISSING** |
| 206 | `FeedlyGetStreamContentsOperationTests` (3 tests) | — | ❌ **MISSING** |
| 207 | `FeedlyGetStreamIdsOperationTests` (3 tests) | — | ❌ **MISSING** |
| 208 | `FeedlyLogoutOperationTests` (4 tests) | — | ❌ **MISSING** |
| 209 | `FeedlyMirrorCollectionsAsFoldersOperationTests` (~4 tests) | — | ❌ **MISSING** |
| 210 | `FeedlyOperationTests` (5 tests) | — | ❌ **MISSING** |
| 211 | `FeedlyOrganiseParsedItemsByFeedOperationTests` (3 tests) | — | ❌ **MISSING** |
| 212 | `FeedlyRefreshAccessTokenOperationTests` (4 tests) | — | ❌ **MISSING** |
| 213 | `FeedlySendArticleStatusesOperationTests` (~10 tests) | — | ❌ **MISSING** |
| 214 | `FeedlySyncStreamContentsOperationTests` (3 tests) | — | ❌ **MISSING** |
| 215 | `FeedlyTextSanitizationTests` (1 test) | — | ❌ **MISSING** |

---

## 3. Ported tests with logic differences

These tests exist in both repos but the C# version is **not fully equivalent** to the
Swift original. Each should be updated to restore full parity.

### 3.1  RssParserTests — missing markdown tests

**Original** (`RSSParserTests.swift`):
```swift
func testMarkdown1() {
    let d = parserData("markdown1", "rss", "https://wordland.social/…")
    let parsedFeed = try! FeedParser.parse(d)!
    for article in parsedFeed.items {
        XCTAssertNotNil(article.markdown)
    }
}

func testMarkdown2() {
    let d = parserData("markdown2", "rss", "https://wordland.social/…")
    let parsedFeed = try! FeedParser.parse(d)!
    for article in parsedFeed.items {
        XCTAssertNotNil(article.markdown)
    }
}
```

**What's missing**: Two tests that verify RSS feeds carrying Markdown content
(`markdown1.rss`, `markdown2.rss`) correctly populate `ParsedItem.Markdown`.
The WinNewsWire `ParsedItem` model already carries a `Markdown` property; the test
just wasn't ported.

**Fix**: Add `Markdown1` and `Markdown2` test methods and copy the two resource
files (`markdown1.rss`, `markdown2.rss`) from
`NetNewsWire/Modules/RSParser/Tests/RSParserTests/Resources/`.

### 3.2  HtmlLinkTests — missing link count assertion

**Original** (`HTMLLinkTests.swift`):
```swift
func testSixColorsLink() {
    let d = parserData("sixcolors", "html", "http://sixcolors.com/")
    let links = RSHTMLLinkParser.htmlLinks(with: d)
    // ...check specific link...
    XCTAssertTrue(found)
    XCTAssertEqual(links.count, 131)   // ← THIS IS MISSING
}
```

**C# version** (`HtmlLinkTests.cs`):
```csharp
[Fact]
public void SixColorsHasExpectedLink()
{
    var links = HtmlLinkParser.ParseLinks(…);
    Assert.Contains(links, l => l.Href == "…" && l.Text == "…");
    // ← No count assertion
}
```

**What's missing**: The `Assert.Equal(131, links.Count)` assertion that guards
against the parser returning too many or too few links. This is a regression
safety net.

**Fix**: Add `Assert.Equal(131, links.Count);` after the `Assert.Contains` call.

### 3.3  OpmlTests — missing error-on-invalid-input test

**Original** (`OPMLTests.swift`):
```swift
func testNotOPML() {
    let d = parserData("DaringFireball", "rss", "http://daringfireball.net/")
    XCTAssertThrowsError(try RSOPMLParser.parseOPML(with: d))
}
```

**What's missing**: A test that feeding non-OPML data to `OpmlParser.Parse` produces
an error (exception or null return). This protects against the parser silently
accepting invalid input.

**Fix**: Add a `NotOpml` test that passes an RSS file to `OpmlParser.Parse` and
asserts it throws or returns null.

### 3.4  ArticleSorterTests — missing groupByFeed sort tests

**Original** (`ArticleSorterTests.swift`) defines 8 tests. The C# version has 6.
Missing are the two most complex sort scenarios:

- `testSortByDateAscendingWithGroupByFeed` — 9 articles across 4 feeds, ascending
  sort grouped by feed. Verifies correct group ordering (alphabetical by feed name)
  and within-group ordering (chronological).

- `testSortByDateDescendingWithGroupByFeed` — same 9 articles, descending sort
  grouped by feed. Verifies reversed within-group ordering while maintaining
  alphabetical group ordering.

Note: `SharedSmokeTests.cs` has two simpler tests that partially cover groupByFeed
behaviour (`ArticleSorter_SortsByDate_Descending` and
`ArticleSorter_GroupByFeed_GroupsThenSortsByDate`), but these use fewer articles
and don't verify the full expected ordering.

**Fix**: Port the original's 9-article test data and full ordering assertions into
`ArticleSorterTests.cs`.

---

## 4. Missing test suites

### 4.1  EntityDecodingTests (HIGH priority)

**Why it matters**: Feed content frequently contains HTML entities like `&#39;`,
`&#8230;`, `&#x2026;`. If the parser's entity decoder has a bug, every article
body and title will display corrupted text.

**Original tests**:
- `test39Decoding`: Verifies `&#39;` decodes to `'` (real bug found by user Manton Reece).
- `testEntities`: Verifies `&#8230;` → `…`, `&#x2026;` → `…`, `&#039;` → `'`,
  `&#167;` → `§`, `&#XA3;` → `£`.

**Target file**: `Tests/Parsers.Tests/EntityDecodingTests.cs`

### 4.2  StripHTMLTests (HIGH priority)

**Why it matters**: HTML stripping is used to generate article summaries, title
previews, and search text. Bugs cause visible garbled text in the timeline.

**Original tests** (8 functional):
- Basic tag stripping, `<script>` removal, `<style>` removal, max character
  truncation, UTF-8 emoji/CJK, whitespace collapsing, real-world HTML from 4 sites,
  expected-output file comparison.

**Prerequisites**: Verify that a `StripHtml` or equivalent utility exists in the
WinNewsWire Core or Parsers module. If not, this test suite should be deferred
until the utility is ported.

**Target file**: `Tests/Core.Tests/StripHtmlTests.cs` (or `Parsers.Tests/`)

### 4.3  String utility tests (MEDIUM priority)

**Original tests** (7 from `String+RSCoreTests.swift`):
- Whitespace collapsing (`"   lots\t\tof   random\n\n…"` → `"lots of random …"`)
- Whitespace trimming (strip leading/trailing whitespace)
- Prefix/suffix stripping (case-sensitive and case-insensitive)
- XML special character escaping (`<`, `>`, `&`, `"` → entities)
- HTTP/HTTPS scheme stripping (`"https://ranchero.com/"` → `"ranchero.com/"`)
- URL normalization (`"feed:daringfireball.net"` → `"http://daringfireball.net/"`,
  `"feeds:…"` → `"https://…"`)

**Target file**: `Tests/Core.Tests/StringExtensionTests.cs`

### 4.4  MacroProcessorTests (MEDIUM priority)

Tests template macro replacement (`[[key]]` → value). The original verifies:
- Standard substitution, multiple macros, nonexistent keys, custom delimiters.
- Empty delimiter rejection (should throw).
- Non-recursive replacement (`[[one]]` substituted with `[[two]]` should NOT expand
  `[[two]]`).

**Prerequisites**: Verify `MacroProcessor` exists in WinNewsWire. It's used for
article rendering templates.

**Target file**: `Tests/Core.Tests/MacroProcessorTests.cs`

### 4.5  MainThreadOperationTests (MEDIUM priority)

Tests an operation queue with dependency tracking. The .NET equivalent may be
`System.Threading.Tasks` or a custom queue. If WinNewsWire uses a custom queue:
- Single operation runs.
- Parent→child dependency ordering.
- Out-of-order add with correct execution order.
- Adding 1000 operations.
- Bulk cancellation.
- Completion callbacks.
- Named cancellation.

**Prerequisites**: Verify an equivalent queue system exists. If WinNewsWire uses
standard `Task` composition, these tests may not be applicable as-is.

**Target file**: `Tests/Core.Tests/OperationQueueTests.cs`

### 4.6  ErrorLogDatabaseTests (MEDIUM priority)

**Original tests** (3):
- Insert an entry and retrieve it with all fields intact.
- Multiple entries return in insertion order.
- Creating a new `ErrorLogDatabase` at an existing path prunes entries beyond 200.

**Target file**: `Tests/Core.Tests/ErrorLogDatabaseTests.cs`

### 4.7  Web utility tests (MEDIUM priority)

**DictionaryTests** (4 tests):
- URL query string encoding with spaces, ampersands, accented chars, emoji.

**StringTests** (1 test):
- HTML escaping (`<foo>"bar"&'baz'` → `&lt;foo&gt;&quot;bar&quot;&amp;&apos;baz&apos;`).

**Target file**: `Tests/Web.Tests/WebUtilityTests.cs`

### 4.8  RSMarkdownTests (LOW priority — blocked on module)

The Markdown module directory exists in WinNewsWire but appears empty.
`PORT_SPEC.md` lists it as "thin wrapper over Markdig." When the module is
implemented, port all ~15 functional tests.

**Target file**: `Tests/Parsers.Tests/MarkdownTests.cs` (or new `Markdown.Tests`)

### 4.9  Account tests (LOW priority — large scope)

| Suite | Tests | Notes |
|---|---|---|
| `AccountCredentialsTest` | 1 | Credential CRUD via Windows Credential Manager |
| `AccountSettingsImporterTests` | 10 | Adapt plist import to WinNewsWire's importer |
| `FeedSettingsImporterTests` | 19 | Adapt plist import to WinNewsWire's importer |
| Feedbin sync (3 files) | 3 | Requires `TestTransport` fake HTTP layer |
| Feedly operations (17 files) | ~50 | Requires porting `FeedlyTestSupport`, all mock services |

The Feedly operation tests are the largest gap. WinNewsWire currently has 11 Feedly
tests focused on serialization and OAuth plumbing. The original has ~50 tests
covering the full operation pipeline (checkpoint, collection sync, stream
ingest, article status send, token refresh, logout, folder mirroring). Porting
these requires building the test support infrastructure (mock services, fake
transport, test account manager).

---

## 5. New tests unique to WinNewsWire

These tests exist in WinNewsWire but have no counterpart in the original. They are
**good additions** and should be kept.

### 5.1  Tests/Parsers.Tests/SmokeParseTests.cs (5 tests)

End-to-end parsing smoke tests for each feed type. Verifies `FeedParser.Parse`
returns a non-null result with the expected `FeedType` and non-empty items.

### 5.2  Tests/Web.Tests/DownloadSessionTests.cs (31 tests across 6 classes)

| Class | Tests | Coverage |
|---|---|---|
| `DownloadSessionTests` | 10 | Single/multi download, 404, 429, cancellation, redirect, progress |
| `SpecialCasesTests` | 5 | OpenRSS, RachelByTheBay, YouTube URL detection, captive portals |
| `CacheControlInfoTests` | 5 | Cache-Control parsing, resume logic, clamped max-age |
| `HttpResponse429Tests` | 4 | Rate-limit response creation, zero/negative retry, resume timing |
| `ConditionalGetInfoTests` | 2 | If-None-Match / If-Modified-Since header application |
| `DownloadSessionResponseTests` | 5 | Status checks, 304, conditional-get info extraction |

### 5.3  Tests/Core.Tests/CoreSmokeTests.cs (4 tests)

| Class | Tests | Coverage |
|---|---|---|
| `ArticlesDbSmoke` | 1 | Round-trip insert → fetch → mark read in ArticlesDatabase |
| `SyncDbSmoke` | 1 | Insert sync statuses → select for processing → verify dequeue |
| `FeedSpecifierTests` | 1 | Best-feed selection prefers user-entered source |
| `TreeSmoke` | 1 | TreeController rebuild populates children from delegate |

### 5.4  Tests/AppShared.Tests (11 tests across 2 classes)

| Class | Tests | Coverage |
|---|---|---|
| `SharedSmokeTests` | 8 | Article sorting, color hash determinism, string formatter truncation, HTML stripping from titles, refresh interval values, MarkStatusCommand undo/redo, ArticleRenderer HTML output |
| `Nnw3ImporterTests` | 3 | XML plist → OPML conversion, binary plist rejection, malformed plist |

### 5.5  Tests/RemoteAccounts.Tests (20 tests across 3 classes)

| Class | Tests | Coverage |
|---|---|---|
| `RemoteAccountTests` | 6 | Delegate type mapping, Feedbin/NewsBlur/ReaderAPI deserialization, factory resolution |
| `RemoteSyncFlushbackTests` | 3 | MarkAsync enqueues sync statuses, flush sends grouped by key/flag, failed group stays pending |
| `FeedlyTests` | 11 | Resource IDs, user streams, mark actions, OAuth URI building, authorize redirect parsing, state mismatch rejection, error surfacing, access token deserialization, entry external URL, entry date, collection deserialization |

---

## 6. Recommended new tests for both codebases

These test areas are absent from **both** repositories and would improve the
WinNewsWire codebase:

| Area | Suggested Tests | Priority |
|---|---|---|
| **OPML export** | Round-trip: build an `OpmlDocument` in memory → export to string → re-parse and compare structure. Verify folder nesting, feed URLs, titles, and special characters survive the round-trip. | High |
| **Database migration** | Create a database at schema version N, run migration to N+1, verify all existing data is preserved and new columns/tables exist. | Medium |
| **FeedFinder** | Given an HTML page with `<link rel="alternate">` tags, verify discovery of feed URLs. Test pages with zero, one, and multiple feed links. Test direct-feed-URL passthrough. | Medium |
| **Concurrent database access** | Multiple tasks writing to ArticlesDatabase and SyncDatabase simultaneously. Verify no SQLite locking errors and data integrity. | Medium |
| **Windows file paths** | Feed cache and database paths with spaces, Unicode characters, and long path prefixes (`\\?\`). Verify file I/O works correctly. | Low |
| **Windows credential store** | If Windows Credential Manager is used: create, retrieve, update, and delete credentials. Verify isolation between accounts. | Low |

---

## 7. Remediation plan

### Phase 1 — Fix parity gaps in existing tests (6 methods)

| Task | File | Effort |
|---|---|---|
| Add `Markdown1` + `Markdown2` tests | `RssParserTests.cs` | Small — copy resource files, add 2 test methods |
| Add link count assertion | `HtmlLinkTests.cs` | Trivial — one line |
| Add `NotOpml` test | `OpmlTests.cs` | Small — one test method |
| Add 2 groupByFeed sort tests | `ArticleSorterTests.cs` | Small — port test data + assertions |

### Phase 2 — Add missing parser & utility tests (~25 methods)

| Task | File | Effort |
|---|---|---|
| Create `EntityDecodingTests.cs` | `Parsers.Tests/` | Small — 2 tests |
| Create `StripHtmlTests.cs` | `Core.Tests/` | Medium — 8 tests, need HTML fixtures |
| Create `StringExtensionTests.cs` | `Core.Tests/` | Medium — 7 tests |
| Create `WebUtilityTests.cs` | `Web.Tests/` | Small — 5 tests |
| Create `ErrorLogDatabaseTests.cs` | `Core.Tests/` | Small — 3 tests |

### Phase 3 — Add missing feature-level tests (~35 methods)

| Task | File | Effort |
|---|---|---|
| Create `MacroProcessorTests.cs` | `Core.Tests/` | Small — 3 tests (if module exists) |
| Create `AccountCredentialTests.cs` | `RemoteAccounts.Tests/` | Medium |
| Expand Feedly operation tests | `RemoteAccounts.Tests/` | Large — requires mock infrastructure |
| Add Feedbin sync tests | `RemoteAccounts.Tests/` | Large — requires test transport |
| Add settings import tests | `AppShared.Tests/` | Medium — adapt plist logic |

### Phase 4 — Markdown & aspirational

| Task | File | Effort |
|---|---|---|
| Port RSMarkdownTests | New `Markdown.Tests/` | Medium — blocked on module |
| OPML export round-trip | `Parsers.Tests/` | Small |
| Database migration tests | `Core.Tests/` | Medium |
| FeedFinder tests | New or `Core.Tests/` | Medium |

---

## Appendix A — Root-level test files

Three `.cs` files exist at the repository root:
- `AtomParserTests.cs`
- `HtmlMetadataTests.cs`
- `RssParserTests.cs`

These appear to be **empty stubs or orphaned files**. They are superseded by the
identically-named files under `Tests/Parsers.Tests/`. Recommend deleting them
to avoid confusion.

## Appendix B — Legend

| Symbol | Meaning |
|---|---|
| ✅ | Test ported with equivalent logic |
| ⚠️ | Test ported but with reduced assertions |
| ❌ | Test missing from WinNewsWire |
| ⏭ | Intentionally skipped (performance benchmark or N/A) |
| ⛔ | Out of scope (macOS/iOS-specific feature) |
