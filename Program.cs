using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace I3X4Kusto
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();

            builder.Services.AddOpenApi();

            builder.Services.AddSwaggerGen();

            builder.Services.AddSingleton<ADXDataService>();

            var app = builder.Build();

            // Configure middleware pipeline
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "i3X Kusto Adapter v1");
            });

            app.UseHttpsRedirection();

            app.MapControllers();

            app.Run();
        }
    }
}
