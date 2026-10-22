# Native graphics workflow

ME: choose View Machine Edition in the application-type selector. SE: choose the user's intended Local Station, Network Station or Network Distributed application, then the exact HMI server where the display resides. Do not guess the SE topology from an ME archive.

The English native UI starts the wizard from the Displays node's **Import and Export** command. Use current accessibility/screenshot observations for each step; indexes and coordinates are not portable instructions. Export the selected display before preparing a native update. To import, choose **Import graphic information into displays**, retain graphics backups, select the batch import file from the returned plan, and update existing objects. The batch entry fixes the display by name and sets `importType="update"`. It contains a filename without a path; its XML is in the same directory.

The target display must be closed for import. Do not discard edits to an existing user display to make that possible. If native errors occur or cancellation interrupts import, collect the error log and re-export the current display before deciding what remains to change. Studio can retain changes already made by an interrupted import.

The plan's application name and language are caller-supplied context. Neither a schema-valid file nor matching re-exported values authenticates which application generated it. Observe the exact native target and language as well as completion. The service intentionally returns `nativeImportStatus="not_observed_by_service"` even when property values match.

Official references:

- [ME Graphics Import Export Wizard](https://www.rockwellautomation.com/en-us/docs/factorytalk-view/16-00-00/me-help-ditamap/about-graphic-displays/graphics-import-export-wizard.html)
- [ME single-display import and interruption behavior](https://www.rockwellautomation.com/en-us/docs/factorytalk-view/16-00-00/me-help-ditamap/about-graphic-displays/graphics-import-export-wizard/import-a-single-display-xml-file.html)
- [Batch import file format](https://www.rockwellautomation.com/en-gb/docs/factorytalk-view/16-00-00/me-help-ditamap/about-graphic-displays/graphic-xml-file/batch-import-file.html)
- [SE graphics import/export selection](https://www.rockwellautomation.com/en-us/docs/factorytalk-view/16-00-00/se-help-ditamap/factorytalk-view-site-edition-help/import-and-export-graphic-xml-files/select-display-import-export-op.html)
