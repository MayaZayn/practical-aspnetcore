/// This file contains the main program logic for a wiki application.
/// 
/// The application provides the following features:
/// - Create, edit, and view wiki pages
/// - Support for search and view history of changes
/// - Support for file attachments
/// - Basic functionality such as page deletion
/// - Ensures pages are indexed for efficient retrieval
/// - Handles user inputs and form submissions
/// 
/// This is implemented using a minimal API approach in .NET Core, emphasizing simplicity and performance.

using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using HtmlBuilders;
using LiteDB;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Scriban;
using System.Globalization;
using System.Text.RegularExpressions;
using static HtmlBuilders.HtmlTags;

const string DisplayDateFormat = "MMMM dd, yyyy";
const string HomePageName = "knowledge-nexus";
const string HtmlMime = "text/html";
const string MessageErrorTemplate = "Error: {_}";
const string MessageInfoTemplate = "Info: {_}";
const string MessageWarningTemplate = "Warning: {_}";

var builder = WebApplication.CreateBuilder();
builder.Services
  .AddSingleton<Wiki>()
  .AddSingleton<Render>()
  .AddAntiforgery()
  .AddMemoryCache();

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseAntiforgery();

// Load home page.
app.MapGet("/", (Wiki wiki, Render render, IAntiforgery antiforgery, HttpContext context) =>
{
    Page? page = wiki.GetPage(HomePageName);

    if (page is not object)
        return Results.Redirect($"/{HomePageName}");

    return Results.Text(render.BuildPage(HomePageName, atBody: () =>
        new[]
        {
          RenderPageContent(page),
          RenderPageAttachments(page),
          A.Href($"/edit?pageName={HomePageName}").Class("uk-button uk-button-default uk-button-small").Append("Edit").ToHtmlString()
        },
        atSidePanel: () => RenderAllPages(wiki, antiforgery, context)
      ).ToString(), HtmlMime);
});

// Create and redirect to a new page.
app.MapGet("/new-page", (string? pageName, Wiki wiki) =>
{
    if (string.IsNullOrEmpty(pageName))
        return Results.Redirect("/");

    // Referenced from https://www.30secondsofcode.org/c-sharp/s/to-kebab-case
    Regex pattern = new(@"[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+");
    var page = string.Join("-", pattern.Matches(pageName!)).ToLower();

    wiki.AddChangeRecord(new ChangeRecord
                        {
                            Name = $"Create Page {pageName}",
                            Date = DateTime.UtcNow
                        });
    
    return Results.Redirect($"/{page}");
});

// Load edit page.
app.MapGet("/edit", (string pageName, HttpContext context,
                    Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    Page? page = wiki.GetPage(pageName);
    if (page is null)
        return Results.NotFound();

    return Results.Text(render.BuildEditorPage(pageName,
        atBody: () =>
        [
            RenderWikiInputForm(new PageInput(page.Id, pageName, page.Content, null),
                        path: $"{pageName}", antiForgery: antiForgery.GetAndStoreTokens(context)),
            RenderPageAttachmentsForEdit(page, antiForgery.GetAndStoreTokens(context))
        ],
        atSidePanel: () =>
        {
            var list = new List<string>();

            // Do not show delete button on home page
            if (!pageName.ToString().Equals(HomePageName, StringComparison.Ordinal))
                list.Add(RenderDeletePageButton(page, antiForgery: antiForgery.GetAndStoreTokens(context)));

            list.Add(Br.ToHtmlString());
            list.AddRange(RenderAllPagesForEditing(wiki));
            
            return list;
        }).ToString(), HtmlMime);
});

// Handle attachment download.
app.MapGet("/attachment", (string fileId, Wiki wiki) =>
{
    var file = wiki.GetFile(fileId);
    if (file is null)
        return Results.NotFound();

    app.Logger.LogInformation(MessageInfoTemplate, $"Attachment {file.Value.meta.Id} - {file.Value.meta.Filename}");

    return Results.File(file.Value.file, file.Value.meta.MimeType);
});

// Load a wiki page.
app.MapGet("/{pageName}", (string pageName, HttpContext context, 
                            Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    pageName ??= "";

    Page? page = wiki.GetPage(pageName);

    if (page is not null)
    {
        return Results.Text(render.BuildPage(pageName, 
            atBody: () =>
            [
                RenderPageContent(page),
                RenderPageAttachments(page),
                Div.Class("last-modified")
                .Append("Last modified: " + page!.LastModifiedUtc
                .ToString(DisplayDateFormat)).ToHtmlString(),
                A.Href($"/edit?pageName={pageName}").Append("Edit").ToHtmlString()
            ],
            atSidePanel: () => RenderAllPages(wiki, antiForgery, context)
        ).ToString(), HtmlMime);
    }
    else
    {
        return Results.Text(render.BuildEditorPage(pageName,
            atBody: () =>
            [
                RenderWikiInputForm(new PageInput(null, pageName, string.Empty, null), 
                            path: pageName, antiForgery: antiForgery.GetAndStoreTokens(context))
            ],
            atSidePanel: () => RenderAllPagesForEditing(wiki)).ToString(), HtmlMime);
    }
});

app.MapPost("/delete-page", async ([FromForm] StringValues id, HttpContext context, 
                                    IAntiforgery antiForgery, Wiki wiki) =>
{
    await antiForgery.ValidateRequestAsync(context);

    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning(MessageWarningTemplate, $"Unable to delete attachment because form Id is missing.");
        return Results.Redirect("/");
    }

    var (isOk, exception) = wiki.DeletePage(Convert.ToInt32(id), HomePageName);

    if (!isOk && exception is not null)
        app.Logger.LogError(exception, MessageErrorTemplate, $"Unable to delete page with id {id}.");
    else if (!isOk)
        app.Logger.LogError(MessageErrorTemplate, $"Unable to delete page with id {id}.");

    wiki.AddChangeRecord(new ChangeRecord
                        {
                            Name = $"Delete Page",
                            Date = DateTime.UtcNow
                        });

    return Results.Redirect("/");
});

app.MapPost("/delete-attachment", async ([FromForm] StringValues id, [FromForm] StringValues pageId,
                                        HttpContext context, IAntiforgery antiForgery, Wiki wiki) =>
{
    await antiForgery.ValidateRequestAsync(context);

    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning(MessageWarningTemplate, $"Unable to delete attachment because form Id is missing.");
        return Results.Redirect("/");
    }

    if (StringValues.IsNullOrEmpty(pageId))
    {
        app.Logger.LogWarning(MessageWarningTemplate, $"Unable to delete attachment because form PageId is missing.");
        return Results.Redirect("/");
    }

    var (isOk, page, exception) = wiki.DeleteAttachment(Convert.ToInt32(pageId), id.ToString());

    if (!isOk)
    {
        if (exception is not null)
            app.Logger.LogError(exception, MessageErrorTemplate, $"Unable to delete page attachment with id {id}");
        else
            app.Logger.LogError(MessageErrorTemplate, $"Unable to delete page attachment with id {id}");

        if (page is not null)
            return Results.Redirect($"/{page.Name}");
        else
            return Results.Redirect("/");
    }

    wiki.AddChangeRecord(new ChangeRecord
                    {
                        Name = $"Delete Attachment",
                        Date = DateTime.UtcNow
                    });
                    
    return Results.Redirect($"/{page!.Name}");
});

// Add or update a wiki page.
app.MapPost("/{pageName}", async (HttpContext context, Wiki wiki, Render render,
                                    IAntiforgery antiForgery, string pageName = "") =>
{
    await antiForgery.ValidateRequestAsync(context);

    PageInput input = PageInput.From(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new PageInputValidator(pageName, HomePageName);
    validator.Validate(input).AddToModelState(modelState, null);

    if (!modelState.IsValid)
    {
        return Results.Text(render.BuildEditorPage(pageName,
            atBody: () =>
            [
                RenderWikiInputForm(input, path: $"{pageName}", 
                    antiForgery: antiForgery.GetAndStoreTokens(context), modelState)
            ],
            atSidePanel: () => RenderAllPages(wiki, antiForgery, context)).ToString(), HtmlMime);
    }

    var (isOk, page, exception) = wiki.SavePage(input);
    if (!isOk)
    {
        if (exception is not null) 
        {
            app.Logger.LogError(exception, MessageErrorTemplate, "Problem in saving page.");
            return Results.Content(exception.StackTrace, "text/html");
        }
        app.Logger.LogError(exception, MessageErrorTemplate, "Problem in saving page.");
        return Results.Problem(MessageErrorTemplate, "Problem in saving page.");
    }

    wiki.AddChangeRecord(new ChangeRecord
                        {
                            Name = $"Edit Page {pageName}",
                            Date = DateTime.UtcNow
                        });

    return Results.Redirect($"/{page!.Name}");
});

app.MapGet("/history", (Wiki wiki, Render render) =>
{
    var history = wiki.GetChangeHistory();
    var html = render.RenderHistoryPage(history).ToString();
    return Results.Content(html, HtmlMime);
});

app.MapPost("/search", ([FromForm] string searchParameter, Wiki wiki, IAntiforgery antiforgery, HttpContext context) => {
    antiforgery.ValidateRequestAsync(context);
    var searchResults = wiki.Search(searchParameter);
    return Results.Content(RenderSearchResults(searchResults), HtmlMime);
});

await app.RunAsync();

// End of the web part.

static string[] RenderAllPages(Wiki wiki, IAntiforgery antiforgery, HttpContext context)
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return [
        $"""
        <button class="uk-button uk-button-default uk-margin-small-right" type="button" uk-toggle="target: #modal-example">Search</button>
        <!-- This is the modal -->
        <div id="modal-example" uk-modal>
            <div class="uk-modal-dialog uk-modal-body">
                <!-- <h2 class="uk-modal-title">Search</h2> -->
                <div class="uk-margin">
                    <form hx-post="/search" hx-target="#searchResults" class="uk-search uk-search-default uk-align-center">
                        <input type="hidden" name="{tokens.FormFieldName}" value="{tokens.RequestToken}">
                        <input name="searchParameter" class="uk-search-input" type="search" placeholder="Search" aria-label="Search">
                        <button class="uk-search-icon-flip" uk-search-icon></button>
                    </form>
                </div>
                <hr>
                <div id="searchResults"></div>
                <p class="uk-text-right">
                    <button class="uk-button uk-button-default uk-modal-close" type="button">Cancel</button>
                </p>
            </div>
        </div>
        <hr>
        """,
        $"""<span class="uk-label">Pages</span>""",
        $"""<ul class="uk-list">""",
        string.Join("",
            wiki.ListAllPages().OrderBy(page => page.Name)
            .Select(page => Li.Append(A.Href(page.Name).Append(page.Name)).ToHtmlString())
        ),
        "</ul>"
    ];
};

static string RenderSearchResults(List<Page> pages) =>
    $"""
        <div class="uk-card uk-card-default uk-card-body">
            <h3 class="uk-card-title">Results</h3>
            <ul class="uk-list uk-list-striped">
    """ + string.Join("",
        pages.OrderBy(page => page.Name)
        .Select(page => Li.Append(A.Href(page.Name).Append(page.Name)).ToHtmlString())
    ) + "</ul>";

static string[] RenderAllPagesForEditing(Wiki wiki)
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    return
    [
        $"""<span class="uk-label">Pages</span>""",
        $"""<ul class="uk-list">""",
        string.Join("",
            wiki.ListAllPages()
                .OrderBy(page => page.Name)
                .Select(page => Li.Append(Div.Class("uk-inline")
                                  .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
                                  .Append(Input.Text.Value($"[{KebabToNormalCase(page.Name)}](/{page.Name})")
                                  .Class("uk-input uk-form-small").Style("cursor", "pointer")
                                  .Attribute("onclick", "copyMarkdownLink(this);"))).ToHtmlString())
      ),
      "</ul>"
    ];
}

static string RenderMarkdown(string markdownText)
{
    var sanitizer = new HtmlSanitizer();
    return sanitizer.Sanitize(Markdown.ToHtml(markdownText, new MarkdownPipelineBuilder()
                                                    .UseSoftlineBreakAsHardlineBreak()
                                                    .UseAdvancedExtensions()
                                                    .Build()));
}

static string RenderPageContent(Page page) => RenderMarkdown(page.Content);

static string RenderDeletePageButton(Page page, AntiforgeryTokenSet antiForgery)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
    HtmlTag id = Input.Hidden.Name("Id").Value(page.Id.ToString());

    var submit = Div.Style("margin-top", "20px")
                    .Append(Button.Class("uk-button uk-button-danger")
                    .Append("Delete Page"));

    var form = Form.Attribute("method", "post")
                   .Attribute("action", $"/delete-page")
                   .Attribute("onsubmit", $"return confirm('Please confirm to delete this page.');")
                   .Append(antiForgeryField)
                   .Append(id)
                   .Append(submit);

    return form.ToHtmlString();
}

static string RenderPageAttachmentsForEdit(Page page, AntiforgeryTokenSet antiForgery)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list");

    HtmlTag CreateEditorHelper(Attachment attachment) =>
                                Span.Class("uk-inline")
                                    .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
                                    .Append(Input.Text.Value($"[{attachment.FileName}](/attachment?fileId={attachment.FileId})")
                                    .Class("uk-input uk-form-small uk-form-width-large")
                                    .Style("cursor", "pointer")
                                    .Attribute("onclick", "copyMarkdownLink(this);")
                                );

    static HtmlTag CreateDelete(int pageId, string attachmentId, AntiforgeryTokenSet antiForgery)
    {
        var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
        var id = Input.Hidden.Name("Id").Value(attachmentId.ToString());
        var name = Input.Hidden.Name("PageId").Value(pageId.ToString());

        var submit = Button.Class("uk-button uk-button-danger uk-button-small").Append(Span.Attribute("uk-icon", "icon: close; ratio: .75;"));
        var form = Form.Style("display", "inline")
                       .Attribute("method", "post")
                       .Attribute("action", $"/delete-attachment")
                       .Attribute("onsubmit", $"return confirm('Please confirm to delete this attachment.');")
                       .Append(antiForgeryField)
                       .Append(id)
                       .Append(name)
                       .Append(submit);

        return form;
    }

    foreach (var attachment in page.Attachments)
    {
        list = list.Append(Li.Append(CreateEditorHelper(attachment))
                             .Append(CreateDelete(page.Id, attachment.FileId, antiForgery))
                            );
    }
    return label.ToHtmlString() + list.ToHtmlString();
}

static string RenderPageAttachments(Page page)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var cardsContainer = Div.Class("uk-grid uk-grid-small uk-child-width-1-3@m uk-margin-top uk-margin-bottom")
                            .Attribute("uk-grid", "");

    foreach (var attachment in page.Attachments)
    {
        var cardBody = Div.Class("uk-card-body attachment-card-body").Append(
            A.Href($"/attachment?fileId={attachment.FileId}").Append(attachment.FileName)
        );

        var card = Div.Class("uk-card uk-card-default uk-margin");

        // Check if the attachment is an image
        if (attachment.MimeType.StartsWith("image"))
        {
            card = card.Append(
                Div.Class("uk-card-media-top").Append(
                    Img.Src($"/attachment?fileId={attachment.FileId}").Attribute("alt", attachment.FileName)
                )
            );
        }

        card = card.Append(cardBody);
        cardsContainer = cardsContainer.Append(Div.Append(card));
    }

    return label.ToHtmlString() + cardsContainer.ToHtmlString();
}

static string RenderWikiInputForm(PageInput input, string path, AntiforgeryTokenSet antiForgery, ModelStateDictionary? modelState = null)
{
    bool IsFieldOk(string key) => modelState!.ContainsKey(key) 
                               && modelState[key]!.ValidationState == ModelValidationState.Invalid;

    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

    var nameField = Div.Append(Label.Class("uk-form-label").Append(nameof(input.Name)))
                       .Append(Div.Class("uk-form-controls")
                                  .Append(Input.Text.Class("uk-input").Name("Name").Value(input.Name)));

    var contentField = Div.Append(Label.Class("uk-form-label").Append(nameof(input.Content)))
                          .Append(Div.Class("uk-form-controls")
                                     .Append(Textarea.Name("Content").Class("uk-textarea").Append(input.Content)));

    var attachmentField = Div.Append(Label.Class("uk-form-label").Append(nameof(input.Attachment)))
                             .Append(Div.Attribute("uk-form-custom", "target: true")
                                        .Append(Input.File.Name("Attachment"))
                                        .Append(Input.Text.Class("uk-input uk-form-width-large")
                                                          .Attribute("placeholder", "Click to select file")
                                                          .ToggleAttribute("disabled", false)));

    if (modelState is not null && !modelState.IsValid)
    {
        if (IsFieldOk("Name"))
        {
            foreach (var error in modelState["Name"]!.Errors)
            {
                nameField = nameField.Append(Div.Class("uk-form-danger uk-text-small").Append(error.ErrorMessage));
            }
        }

        if (IsFieldOk("Content"))
        {
            foreach (var error in modelState["Content"]!.Errors)
            {
                contentField = contentField.Append(Div.Class("uk-form-danger uk-text-small").Append(error.ErrorMessage));
            }
        }
    }

    var submit = Div.Style("margin-top", "20px")
                    .Append(Button.Class("uk-button uk-button-primary")
                                  .Append("Submit"));

    var form = Form.Class("uk-form-stacked")
                   .Attribute("method", "post")
                   .Attribute("enctype", "multipart/form-data")
                   .Attribute("action", $"/{path}")
                   .Append(antiForgeryField)
                   .Append(nameField)
                   .Append(contentField)
                   .Append(attachmentField);

    if (input.Id is not null)
    {
        HtmlTag id = Input.Hidden.Name("Id").Value(input.Id.ToString()!);
        form = form.Append(id);
    }

    form = form.Append(submit);

    return form.ToHtmlString();
}

class Render
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    static string[] MarkdownEditorHead() =>
    [
      $"""<link rel="stylesheet" href="https://unpkg.com/easymde/dist/easymde.min.css">""",
      $"""<script src="https://unpkg.com/easymde/dist/easymde.min.js"></script>"""
    ];

    static string[] MarkdownEditorFoot() =>
    [
      """
        <script>
        var easyMDE = new EasyMDE({
          insertTexts: {
            link: ["[", "]()"]
          }
        });

        function copyMarkdownLink(element) {
          element.select();
          document.execCommand("copy");
        }
        </script>
        """
    ];

    (Template head, Template body, Template layout) _templates = (
        head: Scriban.Template.Parse(
            """
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{ title }}</title>
            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/css/uikit.min.css" />
            {{ header }}
            <style>
                .last-modified { font-size: small; }
                a:visited { color: blue; }
                a:link { color: red; }
                nav {
                    background-color: #212529 !important; 
                    color: white !important;               
                }
                nav * {
                    color: inherit !important;
                }
                .attachment-card-body {
                    padding: 5px !important;
                    max-height: 25px !important; 
                    overflow: hidden;
                    text-align: center;
                }
                .add-page-input {
                    color: black !important;
                }
            </style>
            """
        ),
        body: Scriban.Template.Parse(
            """
            <nav class="uk-navbar-container">
                <div class="uk-container">
                    <div class="uk-navbar">
                        <div class="uk-navbar-left">
                            <ul class="uk-navbar-nav">
                                <li class="uk-active"><a href="/"><span uk-icon="home"></span></a></li>
                            </ul>
                        </div>
                        <div class="uk-navbar-center uk-visible@m">
                            <div class="uk-navbar-item">
                                <form action="/new-page">
                                    <input class="uk-input uk-form-width-large add-page-input" type="text" name="pageName" placeholder="Type desired page title here"></input>
                                    <input type="submit" class="uk-button uk-button-default" value="Add New Page">
                                </form>
                            </div>
                        </div>
                        <div class="uk-navbar-right">
                            <ul class="uk-navbar-nav uk-visible@m">
                                <li class="uk-active"><a href="/history">View History</a></li>
                            </ul>
                            <a class="uk-navbar-toggle uk-hidden@m" uk-navbar-toggle-icon uk-toggle="target: #offcanvas-nav"></a>
                        </div>
                    </div>
                </div>
            </nav>

            <div id="offcanvas-nav" uk-offcanvas="overlay: true">
                <div class="uk-offcanvas-bar">
                    <ul class="uk-nav uk-nav-default">
                        <li class="uk-active"><a href="/history">View History</a></li>
                        <li class="uk-nav-header">Add New Page</li>
                        <li>
                            <form action="/new-page">
                                <input class="uk-input uk-form-width-large uk-margin-small-bottom" type="text" name="pageName" placeholder="Type desired page title here"></input>
                                <input type="submit" class="uk-button uk-button-default" value="Add New Page">
                            </form>
                        </li>
                    </ul>
                </div>
            </div>
            {{ if at_side_panel != "" }}
                <div class="uk-container uk-margin-bottom">
                    <div uk-grid>
                        <div class="uk-width-expand@s uk-width-4-5@m uk-margin-top">
                            <h1>{{ page_name }}</h1>
                            {{ content }}
                        </div>
                        <div class="uk-width-1-1 uk-width-1-3@s uk-width-1-5@m uk-margin-top">
                            <hr class="uk-hidden@m">
                            {{ at_side_panel }}
                        </div>
                    </div>
                </div>
            {{ else }}
                <div class="uk-container uk-margin-top uk-margin-bottom">
                    <h1>{{ page_name }}</h1>
                    {{ content }}
                </div>
            {{ end }}
            <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.0.2/dist/js/bootstrap.bundle.min.js" integrity="sha384-MrcW6ZMFYlzcLA8Nl+NtUVF0sA7MsXsP1UyJoMp4YLEuNSfAP+JcXn/tWtIaxVXM" crossorigin="anonymous"></script>
            <script src="https://unpkg.com/htmx.org@1.9.12" integrity="sha384-ujb1lZYygJmzgSwoxRggbCHcjc0rB2XoQrxeTUQyRjrOnlCoYta87iKBWq3EsdM2" crossorigin="anonymous"></script>
            {{ at_foot }}        
            """
        ),
        layout: Scriban.Template.Parse(
            """
                <!DOCTYPE html>
                <head>
                    {{ head }}
                </head>
                <body>
                    {{ body }}
                </body>
                </html>
            """
        )
    );

    public HtmlString RenderHistoryPage(List<ChangeRecord> history)
    {
        var atBody = new Func<IEnumerable<string>>(() =>
        {
            var html = "<div class=\"uk-card uk-card-default uk-card-large uk-card-body\">";
            html += "<h3 class=\"uk-card-title\">Change History</h3>";
            html += "<ul class=\"uk-list uk-list-striped\">";
            
            foreach (var changeRecord in history)
            {
                html += $"<li>{changeRecord.Date} - {changeRecord.Name}</li>";
            }
            
            html += "</ul></div>";
            return new List<string> { html };
        });

        return BuildPage("Change History", atBody: atBody);
    }

    // Use only when the page requires editor.
    public HtmlString BuildEditorPage(string title, Func<IEnumerable<string>> atBody, Func<IEnumerable<string>>? atSidePanel = null) =>
      BuildPage(
        title,
        atHead: () => MarkdownEditorHead(),
        atBody: atBody,
        atSidePanel: atSidePanel,
        atFoot: () => MarkdownEditorFoot()
        );

    // General page layout building function.
    public HtmlString BuildPage(string title, Func<IEnumerable<string>>? atHead = null, Func<IEnumerable<string>>? atBody = null, 
                                Func<IEnumerable<string>>? atSidePanel = null, Func<IEnumerable<string>>? atFoot = null)
    {
        var head = _templates.head.Render(new
        {
            title,
            header = string.Join("\r", atHead?.Invoke() ?? [""])
        });

        var body = _templates.body.Render(new
        {
            PageName = KebabToNormalCase(title),
            Content = string.Join("\r", atBody?.Invoke() ?? [""]),
            AtSidePanel = string.Join("\r", atSidePanel?.Invoke() ?? [""]),
            AtFoot = string.Join("\r", atFoot?.Invoke() ?? [""])
        });

        return new HtmlString(_templates.layout.Render(new { head, body }));
    }
}

class Wiki(IWebHostEnvironment env, IMemoryCache cache, ILogger<Wiki> logger)
{
    static DateTime Timestamp() => DateTime.UtcNow;
    const string PageCollectionName = "Pages";
    const string AllPagesKey = "AllPages";
    const double CacheAllPagesForMinutes = 30;
    readonly IWebHostEnvironment _env = env;
    readonly IMemoryCache _cache = cache;
    readonly ILogger _logger = logger;
    
    // Get the location of the LiteDB file.
    ConnectionString GetDatabasePath()  
    {
        ConnectionString connectionString = new()
        {
            Filename = Path.Combine(_env.ContentRootPath, "wiki.db"),
            Connection = ConnectionType.Shared
        };
        return connectionString;
    }
    
    // List all the available wiki pages. It is cached for 30 minutes.
    public List<Page> ListAllPages()
    {
        var pages = _cache.Get(AllPagesKey) as List<Page>;

        if (pages is not null)
            return pages;

        using var database = new LiteDatabase(GetDatabasePath());
        var collection = database.GetCollection<Page>(PageCollectionName);
        collection.EnsureIndex(page => page.Name);
        var items = collection.Query().ToList();

        _cache.Set(AllPagesKey, items, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheAllPagesForMinutes)));
        return items;
    }

    // Get a wiki page based on its path.
    public Page? GetPage(string path)
    {
        using var database = new LiteDatabase(GetDatabasePath());
        var collection = database.GetCollection<Page>(PageCollectionName);
        collection.EnsureIndex(page => page.Name);
        return collection.Query()
                         .Where(page => page.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
                         .FirstOrDefault();
    }

    public List<ChangeRecord> GetChangeHistory()
    {
        using var database = new LiteDatabase(GetDatabasePath());
        var collection = database.GetCollection<ChangeRecord>("ChangeHistory");
        var history = collection.Query().ToList();
        return history;
    }

    public List<Page> Search(string searchParameter)
    {
        using var database = new LiteDatabase(GetDatabasePath());
        var collection = database.GetCollection<Page>(PageCollectionName);
        var history = collection.Query()
                                .Where(page => page.Name.Contains(searchParameter, StringComparison.OrdinalIgnoreCase)
                                || page.Content.Contains(searchParameter, StringComparison.OrdinalIgnoreCase))
                                .ToList();
        return history;
    }

    public void AddChangeRecord(ChangeRecord record)
    {
        using var database = new LiteDatabase(GetDatabasePath());
        var collection = database.GetCollection<ChangeRecord>("ChangeHistory");

        // Insert the new change record into the ChangeHistory collection.
        collection.Insert(record);
    }

    // Save or update a wiki page. Cache(AllPagesKey) will be destroyed.
    public (bool isOk, Page? page, Exception? exception) SavePage(PageInput input)
    {
        try
        {
            using var database = new LiteDatabase(GetDatabasePath());
            var collection = database.GetCollection<Page>(PageCollectionName);
            collection.EnsureIndex(page => page.Name);

            Page? existingPage = input.Id.HasValue ? collection.FindOne(page => page.Id == input.Id) : null;

            var sanitizer = new HtmlSanitizer();
            var properName = input.Name.ToString().Trim().Replace(' ', '-').ToLower();

            Attachment? attachment = null;
            if (!string.IsNullOrWhiteSpace(input.Attachment?.FileName))
            {
                attachment = new Attachment
                (
                    FileId: Guid.NewGuid().ToString(),
                    FileName: input.Attachment.FileName,
                    MimeType: input.Attachment.ContentType,
                    LastModifiedUtc: Timestamp()
                );

                try
                {
                    // Open the file stream
                    using var stream = input.Attachment.OpenReadStream();

                    // Upload file to LiteDB FileStorage
                    var res = database.FileStorage.Upload(attachment.FileId, input.Attachment.FileName, stream);
                }
                catch (Exception ex)
                {
                    // Handle any exceptions
                    _logger.LogError(ex, $"33 Error: There is an exception in trying to save page name '{input.Name}'");
                    return (false, null, ex);
                    // Log the exception or take appropriate action
                }


                // using var stream = input.Attachment.OpenReadStream();
                // var res = database.FileStorage.Upload(attachment.FileId, input.Attachment.FileName, stream);
            }

            if (existingPage is null)
            {
                var newPage = new Page
                {
                    Name = sanitizer.Sanitize(properName),
                    // Do not sanitize on input because it will impact some markdown tag such as >.
                    // We do it on the output instead.
                    Content = input.Content,
                    LastModifiedUtc = Timestamp()
                };

                if (attachment is not null)
                    newPage.Attachments.Add(attachment);

                collection.Insert(newPage);
                _cache.Remove(AllPagesKey);

                return (true, newPage, null);
            }
            else
            {
                var updatedPage = existingPage with
                {
                    Name = sanitizer.Sanitize(properName),
                    // Do not sanitize on input because it will impact some markdown tag such as >.
                    // We do it on the output instead.
                    Content = input.Content,
                    LastModifiedUtc = Timestamp()
                };

                if (attachment is not null)
                    updatedPage.Attachments.Add(attachment);

                collection.Update(updatedPage);
                _cache.Remove(AllPagesKey);
                
                return (true, updatedPage, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error: There is an exception in trying to save page name '{input.Name}'");
            return (false, null, ex);
        }
    }

    public (bool isOk, Page? page, Exception? exception) DeleteAttachment(int pageId, string id)
    {
        try
        {
            using var database = new LiteDatabase(GetDatabasePath());
            var collection = database.GetCollection<Page>(PageCollectionName);
            collection.EnsureIndex(page => page.Name);
            var page = collection.FindById(pageId);

            if (page is null)
            {
                _logger.LogWarning($"Warning: Delete attachment operation fails because page id {id} cannot be found in the database.");
                return (false, null, null);
            }

            if (!database.FileStorage.Delete(id))
            {
                _logger.LogWarning($"""
                                        Warning: We cannot delete this file attachment id {id}.
                                        Potential reasons could include non-existent file, file lock, or database corruption. 
                                        Further investigation needed.
                                     """);
                return (false, page, null);
            }

            page.Attachments.RemoveAll(tmpPage => tmpPage.FileId.Equals(id, StringComparison.OrdinalIgnoreCase));

            var updateResult = collection.Update(page);

            if (!updateResult)
            {
                _logger.LogWarning($"Warning: Delete attachment works but updating the page (id {pageId}) attachment list fails.");
                return (false, page, null);
            }

            return (true, page, null);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, $"Error occurred while trying to delete file with id {id}.");
            return (false, null, exception);
        }
    }

    public (bool isOk, Exception? exception) DeletePage(int id, string homePageName)
    {
        try
        {
            using var database = new LiteDatabase(GetDatabasePath());
            var collection = database.GetCollection<Page>(PageCollectionName);
            collection.EnsureIndex(page => page.Name);
            var page = collection.FindById(id);

            if (page is null)
            {
                _logger.LogWarning($"Warinig: Delete operation fails because page id {id} cannot be found in the database.");
                return (false, null);
            }

            if (page.Name.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Warning: Page id {id}  is a home page and delete operation on home page is not allowed.");
                return (false, null);
            }

            // Delete all attachments.
            foreach (var a in page.Attachments)
            {
                database.FileStorage.Delete(a.FileId);
            }

            if (collection.Delete(id))
            {
                _cache.Remove(AllPagesKey);
                return (true, null);
            }

            _logger.LogWarning($"""
                                Warning: Failed to delete page id {id}. 
                                Potential issues could include database constraints, file locks, or internal errors.
                                """);
            return (false, null);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, $"Error occurred while trying to delete file with id {id}.");
            return (false, exception);
        }
    }

    // Return null if file cannot be found.
    public (LiteFileInfo<string> meta, byte[] file)? GetFile(string fileId)
    {
        using var database = new LiteDatabase(GetDatabasePath());

        var meta = database.FileStorage.FindById(fileId);
        if (meta is null)
            return null;

        using var stream = new MemoryStream();
        database.FileStorage.Download(fileId, stream);
        return (meta, stream.ToArray());
    }
}

record Page
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime LastModifiedUtc { get; set; }

    public List<Attachment> Attachments { get; set; } = [];
}

record ChangeRecord
{
    public required string Name { get; set; }
    public required DateTime Date { get; set; }
}

record Attachment
(
    string FileId,

    string FileName,

    string MimeType,

    DateTime LastModifiedUtc
);

record PageInput(int? Id, string Name, string Content, IFormFile? Attachment)
{
    public static PageInput From(IFormCollection form)
    {
        var (id, name, content) = (form["Id"], form["Name"], form["Content"]);

        int? pageId = null;

        if (!StringValues.IsNullOrEmpty(id))
            pageId = Convert.ToInt32(id);

        IFormFile? file = form.Files["Attachment"];

        return new PageInput(pageId, name!, content!, file);
    }
}

class PageInputValidator : AbstractValidator<PageInput>
{
    public PageInputValidator(string pageName, string homePageName)
    {
        RuleFor(page => page.Name).NotEmpty().WithMessage("Name is required.");
        if (pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            RuleFor(page => page.Name).Must(name => name.Equals(homePageName))
                                      .WithMessage($"You cannot modify home page name. Please keep it {homePageName}.");

        RuleFor(page => page.Content).NotEmpty().WithMessage("Content is required.");
    }
}