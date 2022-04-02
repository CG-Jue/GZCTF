﻿namespace CTFServer.Models.Request.Teams;

public class TeamInfoModel
{
    /// <summary>
    /// 队伍 Id
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 队伍名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 队伍签名
    /// </summary>
    public string? Bio { get; set; }

    /// <summary>
    /// 头像链接
    /// </summary>
    public string? Avatar { get; set; }

    /// <summary>
    /// 是否锁定
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 队伍成员
    /// </summary>
    public List<BasicUserInfoModel> Members { get; set; } = new();

    public static TeamInfoModel FromTeam(Team team)
        => new()
        {
            Id = team.Id,
            Name = team.Name,
            Bio = team.Bio,
            Avatar = team.AvatarUrl,
            Locked = team.Locked,
            Members = team.Members.Select(m => new BasicUserInfoModel()
                       {
                           Id = m.Id,
                           Bio = m.Bio,
                           UserName = m.UserName,
                           Avatar = m.AvatarUrl,
                           Captain = m.Id == team.CaptainId
                       }).ToList()
        };
}
