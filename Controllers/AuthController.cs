using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using Photon.Auth.Records;
using User = Photon.Auth.Records.User;

namespace Photon.Auth.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(
    IConfiguration config,
    GitHubClient gitHubClient,
    ILogger<AuthController> logger,
    HttpClient httpClient) : ControllerBase
{
    private string CreateToken(User user)
    {
        logger.LogInformation("{0}", user);
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Uri, user.ProfilePicture)
        };
        var token = new JwtSecurityToken(config["Jwt:Issuer"],
            config["Jwt:Audience"],
            claims,
            expires: DateTime.Now.AddDays(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [HttpGet("login/github")]
    public async Task<ActionResult<LoginResponse>> LoginGithub([FromQuery] LoginRequest request)
    {
        var tokenRequest =
            new OauthTokenRequest(config["Github:ClientId"], config["Github:ClientSecret"], request.Code);
        var token = await gitHubClient.Oauth.CreateAccessToken(tokenRequest);
        if (token.Error is not null)
            return BadRequest(token.ErrorDescription + " " + token.ErrorUri);
        var creds = new Credentials(token.AccessToken, AuthenticationType.Bearer);
        gitHubClient.Credentials = creds;
        var githubUser = await gitHubClient.User.Current();
        var emailAddresses = await gitHubClient.User.Email.GetAll();
        var primary = emailAddresses.First(e => e.Primary);
        var user = new User(githubUser.Name, primary.Email, githubUser.AvatarUrl);
        return Ok(new LoginResponse(user, CreateToken(user)));
    }

    [HttpGet("login/discord")]
    public async Task<ActionResult<LoginResponse>> LoginDiscord([FromQuery] LoginRequest request)
    {
        httpClient.BaseAddress = new Uri("https://discord.com/");
        var data = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = request.Code,
            ["redirect_uri"] = config["Discord:RedirectUri"]!
        };
        var content = new FormUrlEncodedContent(data);
        var byteArray = Encoding.ASCII.GetBytes($"{config["Discord:ClientId"]}:{config["Discord:ClientSecret"]}");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
        var response = await httpClient.PostAsync("api/v10/oauth2/token", content);
        var responseString = await response.Content.ReadAsStringAsync();
        var responseContent = JsonSerializer.Deserialize<DiscordTokenResponse>(responseString);
        var token = responseContent?.AccessToken;
        if (token is null)
            return BadRequest("Invalid code");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var userResponse = await httpClient.GetAsync("api/v10/users/@me");
        var userContent = await userResponse.Content.ReadFromJsonAsync<DiscordUserResponse>();
        if (userContent?.Username is null)
            return BadRequest("Invalid token");
        var user = new User(userContent.Username, userContent.Email,
            "https://cdn.discordapp.com/avatars/" + userContent.Id + "/" + userContent.Avatar + ".png");
        return Ok(new LoginResponse(user, CreateToken(user)));
    }


    [HttpGet("validate")]
    public ActionResult<User> Validate([FromQuery] string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(config["Jwt:Key"]!);
        var claims = tokenHandler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidIssuer = config["Jwt:Issuer"],
            ValidAudience = config["Jwt:Audience"]
        }, out _);
        return claims == null
            ? Unauthorized()
            : Ok(new User
            (
                Name: claims.Claims.First().Value,
                Email: claims.Claims.Skip(1).First().Value,
                ProfilePicture: claims.Claims.Skip(2).First().Value
            ));
    }
}