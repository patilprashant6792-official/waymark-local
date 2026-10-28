-- The package tools serve NuGet and PyPI under one set of names, the project command tool covers Python as well as .NET, and the module that
-- holds the package tools is called 'packages'. The owner's per-tool settings and edited guides follow the new names. OR IGNORE keeps a row
-- that already exists under the new name (a fresh database has none of the old ones).

UPDATE OR IGNORE tool_settings SET tool_name = 'pkg_search' WHERE tool_name = 'nuget_packages';
UPDATE OR IGNORE tool_settings SET tool_name = 'pkg_outline' WHERE tool_name = 'nuget_outline';
UPDATE OR IGNORE tool_settings SET tool_name = 'pkg_find' WHERE tool_name = 'nuget_find';
UPDATE OR IGNORE tool_settings SET tool_name = 'pkg_expand' WHERE tool_name = 'nuget_expand';
UPDATE OR IGNORE tool_settings SET tool_name = 'cmd_project' WHERE tool_name = 'cmd_dotnet';

UPDATE OR IGNORE tool_guides SET module = 'packages' WHERE module = 'nuget';
UPDATE OR IGNORE tool_guide_deliveries SET module = 'packages' WHERE module = 'nuget';
