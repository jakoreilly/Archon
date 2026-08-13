-- File header: this script creates the Widgets table and its accessor procedure.
/* Widgets: the core catalog table. */
CREATE TABLE [dbo].[Widgets] (
    [WidgetId] INT            IDENTITY (1, 1) NOT NULL,
    [Name]     NVARCHAR (200) NOT NULL,
    CONSTRAINT [PK_Widgets] PRIMARY KEY ([WidgetId])
)
GO
-- usp_GetWidget: fetch a single widget by id.
CREATE PROCEDURE [dbo].[usp_GetWidget]
@WidgetId INT
AS
BEGIN
    SELECT WidgetId, /* the surrogate key */
           Name
    FROM   [dbo].[Widgets]
    WHERE  WidgetId = @WidgetId;
END
