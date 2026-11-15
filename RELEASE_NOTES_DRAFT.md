# Release notes draft

This draft is local review material. Assign the actual shipped version and include only features validated for that release before publishing to GitHub.

- Saved channel libraries open sooner, with recently opened account libraries reused for faster switching.
- Each account can remember whether to update its playlist in the background when opened. Saved channels remain available while a refresh runs.
- Update prompts now include What's new, the release publication date, and a link to the exact offered release. Long notes can be scrolled and copied; missing notes show a helpful fallback.
- Fixed an account-switch error after series loading caused by a disposed cancellation source.

Automatic installation and user-data migration are still planned; the current Update now action opens the GitHub release.
