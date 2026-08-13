CREATE TABLE [dbo].[Customers] (
    [CustomerId] INT IDENTITY(1,1) NOT NULL,
    [Name] NVARCHAR(200) NOT NULL,
    [Email] NVARCHAR(320) NULL,
    CONSTRAINT [PK_Customers] PRIMARY KEY ([CustomerId])
);
GO

CREATE TABLE [dbo].[Orders] (
    [OrderId] INT IDENTITY(1,1) NOT NULL,
    [CustomerId] INT NULL,
    [Total] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY ([OrderId]),
    CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers]([CustomerId])
);
GO

CREATE INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] ([CustomerId]);
GO

CREATE VIEW [dbo].[vw_OrderSummary] AS
SELECT o.OrderId, o.Total, c.Name
FROM [dbo].[Orders] o
JOIN [dbo].[Customers] c ON o.CustomerId = c.CustomerId;
GO

CREATE PROCEDURE [dbo].[usp_GetOrdersForCustomer]
    @CustomerId INT
AS
BEGIN
    SELECT * FROM [dbo].[Orders] WHERE CustomerId = @CustomerId;
END
GO

CREATE TABLE [dbo].[NoPkTable] (
    [SomeValue] NVARCHAR(50) NULL
);
GO

CREATE LOGIN [app_user] WITH PASSWORD = 'DoNotLeak123!';
GO
