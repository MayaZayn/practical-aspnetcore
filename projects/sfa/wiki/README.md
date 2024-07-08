# Wiki

This is a Single File Application (SFA) that provide wiki functionality.

- It supports markdown
- You can rename pages
- It is stored using LiteDB
- It has a nice markdown editor
- You can upload attachments in every page
- You can delete attachments
- You can delete pages
- It has pages and attachment markdown linking helpers

All the code (810 lines) is contained within `Program.cs`. 

Used libraries:

* Storage - [LiteDB](https://github.com/mbdavid/LiteDB).
* Text Template - [Scriban](https://github.com/lunet-io/scriban).
* Markdown Support - [Markdig](https://github.com/lunet-io/markdig).
* Validation - [FluentValidation](https://github.com/FluentValidation/FluentValidation).
* Html Generation - [HtmlBuilders](https://github.com/amoerie/HtmlBuilders).
* Markdown Editor - [EasyMDE](https://github.com/Ionaru/easy-markdown-editor).
* Sanitizing Input - [HtmlSanitizer](https://github.com/mganss/HtmlSanitizer).

Updates:
- Removed warnings
- Removed unnecassary (! and $ operators)
- Made lines shorter
- Added summary at the beginning of the file
- Added comments for better understanding
- Removed unnecassary comments
- Added spaces
- Adjusted indentation
- Made templates for logging messages
- Renamed vague variables
- Used raw string (re check)
- Separate various . operators on different lines
- Change constructor in Wiki class to primary constructor
- Improve some error messages
- Fix typos
- Add app.UseAntiforgery()
- Add search functionality
- Add change log feature
- Made it responsive
- Add view image uploaded via markdown feature
- Fix database shared issue      
- Change navbar color
  
**Screenshot**
![screenshot of the running wiki](fanon.png)

dotnet6
