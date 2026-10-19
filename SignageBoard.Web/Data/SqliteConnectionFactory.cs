namespace SignageBoard.Web.Data
{
    public sealed class SqliteConnectionFactory(string connectionString)
    {
        public SqliteConnection Create()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            return conn;
        }
    }
}
