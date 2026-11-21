param([string]$ProgID)
$ErrorActionPreference = 'Stop'
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$app = New-Object -ComObject $ProgID
Assert ($app.Name -eq 'WinNewsWire') 'Application name'
Assert ($app.Version -eq '9.8.7') 'Application version'
$account = $app.Accounts.Item(1)
Assert ($account.ID -eq 'fixture-account') 'Account ID'
Assert ($app.Accounts.ByID($account.ID).Name -eq 'Fixture Account') 'ID lookup'
Assert ($app.Accounts.ByName('Fixture Account').ID -eq $account.ID) 'Name lookup'
Assert ($account.AccountType -eq 'Locl') 'Account type'
Assert ($account.AllFeeds.Count -eq 2) 'All feeds includes nested'
Assert ($account.Feeds.Count -eq 1) 'Root feeds excludes nested'
$feeds = @()
foreach ($feed in $account.AllFeeds) { $feeds += $feed.Name }
Assert ($feeds.Count -eq 2) 'IEnumVARIANT'
Assert ($account.AllWebFeeds.Count -eq 2) 'allWebFeeds synonym'
Assert ($app.WebFeeds.Count -eq 1) 'webFeed synonym'
$feed = $account.Feeds.ByName('Fixture Feed')
Assert ($feed.Url -eq 'https://example.test/rss') 'Feed URL'
Assert ($feed.HomePageUrl -eq 'https://example.test/') 'Feed homepage'
Assert ($feed.IconUrl -eq 'https://example.test/icon.png') 'Feed icon'
Assert ($feed.FaviconUrl -eq 'https://example.test/favicon.ico') 'Feed favicon'
Assert ($feed.OpmlRepresentation.Contains('example.test/rss')) 'Feed OPML'
Assert ($account.OpmlRepresentation.Contains('Fixture Folder')) 'Account OPML'
$author = $feed.Authors.Item(1)
Assert ($author.Name -eq 'Fixture Author') 'Author name'
Assert ($author.Url -eq 'https://example.test/author') 'Author URL'
Assert ($author.EmailAddress -eq 'author@example.test') 'Author email'
Assert ($author.Email -eq $author.EmailAddress) 'Email synonym'
Assert ($author.AvatarUrl -eq 'https://example.test/avatar.png') 'Author avatar'
Assert ($feed.Authors.ByID($author.ID).Name -eq $author.Name) 'Author ID'
$article = $feed.Articles.ByID('publisher-id')
Assert ($article.Title -eq 'Fixture Article') 'Article title'
Assert ($article.ID -eq 'publisher-id') 'Publisher ID rather than database ID'
Assert ($article.Url -eq 'https://example.test/article') 'Article preferred URL'
Assert ($article.Permalink -eq $article.Url) 'Permalink'
Assert ($article.ExternalUrl -eq 'https://example.test/external') 'External URL'
Assert ($article.Contents -eq 'Body') 'Plain text'
Assert ($article.Html -eq '<p>Body</p>') 'HTML'
Assert ($article.Summary -eq 'Summary') 'Summary'
Assert ($article.ImageUrl -eq 'https://example.test/image.png') 'Image'
Assert ($article.PublishedDate -is [DateTime]) 'VT_DATE'
Assert ($article.ModifiedDate -is [DateTime]) 'Modified VT_DATE'
Assert ($article.ArrivedDate -is [DateTime]) 'Arrived VT_DATE'
Assert ($article.Feed.ID -eq $feed.ID) 'Containing feed'
Assert ($article.Authors.Item(1).ID -eq $author.ID) 'Article authors'
Assert (-not $article.Deleted) 'Deleted is false upstream'
$article.Read = $true
$article.Starred = $true
Assert ($feed.Articles.ByID('publisher-id').Read) 'Read mutation persisted'
Assert ($feed.Articles.ByID('publisher-id').Starred) 'Star mutation persisted'
Assert ($app.CurrentArticle.ID -eq 'publisher-id') 'Current article'
Assert ($app.SelectedArticles.Count -eq 1) 'Selected articles'
Assert ($app.Articles.Count -eq 1) 'Application articles are selected'
Assert ($app.SelectedFeeds.Count -eq 1) 'Selected feeds'
$account.Active = $false
Assert ($app.Feeds.Count -eq 0) 'Inactive account omitted from root feeds'
$account.Active = $true
$folder = $account.Folders.Item(1)
Assert ($folder.ID -is [int]) 'Numeric folder ID'
Assert ($account.Folders.ByID($folder.ID).Name -eq 'Fixture Folder') 'Folder ID lookup'
Assert ($folder.Feeds.Count -eq 1) 'Folder feeds'
Assert ($folder.WebFeeds.Count -eq 1) 'Folder webFeed synonym'
Assert ($folder.Articles.Count -eq 0) 'Folder articles'
Assert ($folder.OpmlRepresentation.Contains('Nested Feed')) 'Folder OPML'
$made = $account.CreateFolder('Made by COM')
$nested = $made.CreateFeed('https://example.test/made', 'Made Feed')
Assert ($nested.Name -eq 'Made Feed') 'Create feed completion'
Assert ($made.Feeds.Count -eq 1) 'Create feed target'
Assert ($app.Exists($nested)) 'Standard exists'
Assert ($app.Count($made.Feeds) -eq 1) 'Standard count'
$nested.Delete()
Assert (-not $nested.Exists) 'Deleted object no longer exists'
Assert ($made.Feeds.Count -eq 0) 'Collection remains live'
$app.Delete($made)
Assert (-not $app.Exists($made)) 'Delete folder'
$properties = New-Object -ComObject Scripting.Dictionary
$properties.Add('name', 'Generic Make')
$made = $app.Make('folder', $account, [Type]::Missing, $properties)
$properties.RemoveAll()
$properties.Add('name', 'Generic Feed')
$properties.Add('url', 'https://example.test/generic')
$nested = $app.Make('webFeed', $made, [Type]::Missing, $properties)
Assert ($nested.Name -eq 'Generic Feed') 'Make with properties'
$made.Feeds.Where('Name', 'Generic Feed').Delete()
Assert ($made.Feeds.Count -eq 0) 'Filtered bulk delete'
$made.Delete()
$app.OpenLocation('https://example.test/rss')
$failed = $false
try { $null = $feed.Articles.ByID('database-id') } catch { $failed = $true }
Assert $failed 'Invalid article ID must fail'
$failed = $false
try { $account.Name = 'Forbidden' } catch { $failed = $true }
Assert $failed 'Read-only property must fail'
$failed = $false
try { $app.Make('account') } catch { $failed = $true }
Assert $failed 'Unsupported creation must fail'
Assert (-not $account.Feeds.Exists('Missing Feed')) 'Missing name'
Assert (-not $account.Feeds.Exists(0)) 'One-based bounds'
Start-Sleep -Seconds 3
Assert ($account.Name -eq 'Fixture Account') 'Server survives idle timer with live objects'
'COM PARITY OK'
