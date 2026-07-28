CREATE PROCEDURE dbo.DoTheThing AS
BEGIN
    CREATE TABLE #working (Id INT);
    SELECT Id FROM dbo.Orders;
END
