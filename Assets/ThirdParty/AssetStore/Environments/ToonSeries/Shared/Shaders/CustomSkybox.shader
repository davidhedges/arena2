// Made with Amplify Shader Editor v1.9.9.12
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "Toon/CustomSkybox"
{
	Properties
	{
		[Gamma][Header(Cubemap)] _TintColor( "Tint Color", Color ) = ( 0.5, 0.5, 0.5, 1 )
		_Exposure( "Exposure", Range( 0, 8 ) ) = 1
		[NoScaleOffset] _Tex( "Cubemap (HDR)", CUBE ) = "black" {}
		[Header(Rotation)][Toggle( _ENABLEROTATION_ON )] _EnableRotation( "Enable Rotation", Float ) = 0
		[IntRange] _Rotation( "Rotation", Range( 0, 360 ) ) = 0
		_RotationSpeed( "Rotation Speed", Float ) = 1
		[Header(Fog)][Toggle( _ENABLEFOG_ON )] _EnableFog( "Enable Fog", Float ) = 0
		_FogOpacity( "Fog Opacity", Range( 0, 1 ) ) = 0.5
		_FogHeight( "Fog Height", Range( 0, 1 ) ) = 1
		_FogSmoothness( "Fog Smoothness", Range( 0.01, 1 ) ) = 0.01
		[HideInInspector] _Tex_HDR( "DecodeInstructions", Vector ) = ( 0, 0, 0, 0 )
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Opaque"  "Queue" = "Geometry+0" "IsEmissive" = "true"  }
		Cull Back
		CGPROGRAM
		#include "UnityShaderVariables.cginc"
		#pragma target 3.5
		#pragma shader_feature_local _ENABLEFOG_ON
		#pragma shader_feature_local _ENABLEROTATION_ON
		#define ASE_VERSION 19912
		#pragma surface surf Unlit keepalpha addshadow fullforwardshadows nofog vertex:vertexDataFunc 
		struct Input
		{
			float3 vertexToFrag50;
			float3 worldPos;
		};

		uniform half4 _Tex_HDR;
		uniform samplerCUBE _Tex;
		uniform half _Rotation;
		uniform half _RotationSpeed;
		uniform half4 _TintColor;
		uniform half _Exposure;
		uniform half _FogHeight;
		uniform half _FogSmoothness;
		uniform half _FogOpacity;


		inline half3 DecodeHDR57( float4 Data )
		{
			return DecodeHDR(Data, _Tex_HDR);
		}


		void vertexDataFunc( inout appdata_full v, out Input o )
		{
			UNITY_INITIALIZE_OUTPUT( Input, o );
			float3 ase_positionWS = mul( unity_ObjectToWorld, v.vertex );
			float lerpResult14 = lerp( 1.0 , ( unity_OrthoParams.y / unity_OrthoParams.x ) , unity_OrthoParams.w);
			half CAMERA_MODE17 = lerpResult14;
			float3 appendResult34 = (float3(ase_positionWS.x , ( ase_positionWS.y * CAMERA_MODE17 ) , ase_positionWS.z));
			float3 normalizeResult39 = normalize( appendResult34 );
			float3 appendResult27 = (float3(cos( radians( ( _Rotation + ( _Time.y * _RotationSpeed ) ) ) ) , 0.0 , ( sin( radians( ( _Rotation + ( _Time.y * _RotationSpeed ) ) ) ) * -1.0 )));
			float3 appendResult29 = (float3(0.0 , CAMERA_MODE17 , 0.0));
			float3 appendResult30 = (float3(sin( radians( ( _Rotation + ( _Time.y * _RotationSpeed ) ) ) ) , 0.0 , cos( radians( ( _Rotation + ( _Time.y * _RotationSpeed ) ) ) )));
			float3 normalizeResult33 = normalize( ase_positionWS );
			#ifdef _ENABLEROTATION_ON
				float3 staticSwitch46 = mul( float3x3( appendResult27, appendResult29, appendResult30 ), normalizeResult33 );
			#else
				float3 staticSwitch46 = normalizeResult39;
			#endif
			o.vertexToFrag50 = staticSwitch46;
		}

		inline half4 LightingUnlit( SurfaceOutput s, half3 lightDir, half atten )
		{
			return half4 ( 0, 0, 0, s.Alpha );
		}

		void surf( Input i , inout SurfaceOutput o )
		{
			half4 Data57 = texCUBE( _Tex, i.vertexToFrag50 );
			half3 localDecodeHDR57 = DecodeHDR57( Data57 );
			half4 CUBEMAP65 = ( float4( localDecodeHDR57 , 0.0 ) * unity_ColorSpaceDouble * _TintColor * _Exposure );
			float3 ase_positionWS = i.worldPos;
			float3 normalizeResult35 = normalize( ase_positionWS );
			float lerpResult62 = lerp( saturate( pow(  (0.0 + ( abs( normalizeResult35.y ) - 0.0 ) * ( 1.0 - 0.0 ) / ( _FogHeight - 0.0 ) ) , ( 1.0 - _FogSmoothness ) ) ) , 0.0 , _FogOpacity);
			half FOG_MASK66 = lerpResult62;
			float4 lerpResult70 = lerp( unity_FogColor , CUBEMAP65 , FOG_MASK66);
			#ifdef _ENABLEFOG_ON
				float4 staticSwitch72 = lerpResult70;
			#else
				float4 staticSwitch72 = CUBEMAP65;
			#endif
			o.Emission = staticSwitch72.rgb;
			o.Alpha = 1;
		}

		ENDCG
	}
	Fallback Off
	CustomEditor "AmplifyShaderEditor.MaterialInspector"
}
/*ASEBEGIN
Version=19912
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":1,"pos":[-2112,-704],"params":["Inherit","False","2411","608","Cubemap Coordinates","26","39","37","34","33","32","30","29","28","27","26","25","23","22","21","20","19","18","16","15","12","9","7","5","4","3","2","CUBEMAP","1,1,1,1","0","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":6,"pos":[-2112,-1008],"params":["Inherit","False","860","219","Switch between Perspective / Orthographic camera","4","14","11","10","8","CAMERA MODE","1,1,1,1","0","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":2,"pos":[-2064,-400],"params":["Half","False","Property","_RotationSpeed","Rotation Speed","5","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleTimeNode, AmplifyShaderEditor","id":3,"pos":[-2064,-528],"params":["Inherit","False","1","0","FLOAT","1","False","5","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":4,"pos":[-2064,-656],"params":["Half","False","Property","_Rotation","Rotation","4","1","[IntRange]","Create","True","0","0","0","False","0","False","Object","-1","","0","102","0","360","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":5,"pos":[-1808,-528],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.OrthoParams, AmplifyShaderEditor","id":8,"pos":[-2064,-960],"params":["Inherit","False","0","5","FLOAT4","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.SimpleAddOpNode, AmplifyShaderEditor","id":7,"pos":[-1680,-656],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":10,"pos":[-1616,-960],"params":["Half","False","Constant","_Float7","Float 7","47","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleDivideOpNode, AmplifyShaderEditor","id":11,"pos":[-1760,-960],"params":["Inherit","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RadiansOpNode, AmplifyShaderEditor","id":9,"pos":[-1552,-656],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":14,"pos":[-1424,-960],"params":["Inherit","False","3","0","FLOAT","1","False","1","FLOAT","0.5","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":24,"pos":[-2112,-16],"params":["Inherit","False","1898","485","Fog Coords on Screen","15","62","56","54","53","52","49","47","45","44","42","41","40","36","35","31","FOG EFFECT","0.4653275,0.4980392,0,1","0","0"]}
{"type":"AmplifyShaderEditor.RelayNode, AmplifyShaderEditor","id":12,"pos":[-1392,-400],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":17,"pos":[-1168,-960],"params":["Half","False","CAMERA_MODE","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":15,"pos":[-1040,-528],"params":["Half","False","Constant","_Float26","Float 26","50","0","Create","True","0","0","0","False","0","False","Object","-1","","-1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SinOpNode, AmplifyShaderEditor","id":16,"pos":[-1040,-592],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":18,"pos":[-848,-592],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":19,"pos":[-1040,-432],"params":["Inherit","False","17","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":20,"pos":[-1040,-352],"params":["Half","False","Constant","_Float27","Float 27","50","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CosOpNode, AmplifyShaderEditor","id":21,"pos":[-1040,-208],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SinOpNode, AmplifyShaderEditor","id":22,"pos":[-1040,-272],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":23,"pos":[-400,-256],"params":["Inherit","False","17","CAMERA_MODE","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CosOpNode, AmplifyShaderEditor","id":25,"pos":[-1040,-656],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.WorldPosInputsNode, AmplifyShaderEditor","id":31,"pos":[-2064,32],"params":["Float","False","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.WorldPosInputsNode, AmplifyShaderEditor","id":26,"pos":[-400,-432],"params":["Float","False","0","4","FLOAT3","0","FLOAT","1","FLOAT","2","FLOAT","3"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":27,"pos":[-656,-656],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":28,"pos":[-144,-272],"params":["Inherit","False","2","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":29,"pos":[-656,-464],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","1","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":30,"pos":[-656,-272],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":35,"pos":[-1808,32],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.MatrixFromVectors, AmplifyShaderEditor","id":32,"pos":[-400,-656],"params":["Inherit","False","FLOAT3x3","0","4","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","1","FLOAT3x3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":33,"pos":[-144,-528],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.DynamicAppendNode, AmplifyShaderEditor","id":34,"pos":[-16,-400],"params":["Inherit","False","FLOAT3","4","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","3","FLOAT","0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.BreakToComponentsNode, AmplifyShaderEditor","id":36,"pos":[-1616,32],"params":["Inherit","False","FLOAT3","1","0","FLOAT3","0,0,0","False","16","FLOAT","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT","5","FLOAT","6","FLOAT","7","FLOAT","8","FLOAT","9","FLOAT","10","FLOAT","11","FLOAT","12","FLOAT","13","FLOAT","14","FLOAT","15"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":38,"pos":[368,-560],"params":["Inherit","False","394","188","Enable Rotation","1","46","","0,0.7386749,1,1","0","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":37,"pos":[112,-576],"params":["Inherit","False","2","2","0","FLOAT3x3","0,0,0,1,1,1,1,0,1","False","1","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.NormalizeNode, AmplifyShaderEditor","id":39,"pos":[144,-400],"params":["Inherit","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":40,"pos":[-2064,224],"params":["Half","False","Property","_FogHeight","Fog Height","8","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0.21","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":41,"pos":[-2064,352],"params":["Half","False","Property","_FogSmoothness","Fog Smoothness","9","0","Create","True","0","0","0","False","0","False","Object","-1","","0.01","0.34","0.01","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.AbsOpNode, AmplifyShaderEditor","id":44,"pos":[-1296,32],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":45,"pos":[-1296,160],"params":["Half","False","Constant","_Float","Float","55","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":42,"pos":[-1296,256],"params":["Half","False","Constant","_Float0","Float 0","55","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":46,"pos":[416,-496],"params":["Float","False","Property","_EnableRotation","Enable Rotation","3","0","Create","True","0","0","0","False","1","Header(Rotation)","False","","0","0","1","True","","Toggle","2","Key0","Key1","Create","True","True","All","9","1","FLOAT3","0,0,0","False","0","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT3","0,0,0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.OneMinusNode, AmplifyShaderEditor","id":47,"pos":[-1040,352],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.TFHCRemapNode, AmplifyShaderEditor","id":49,"pos":[-1104,32],"params":["Inherit","False","5","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","1","False","3","FLOAT","0","False","4","FLOAT","1","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":48,"pos":[1200,-576],"params":["Inherit","False","1115","565","Base","7","73","63","59","58","57","55","51","","0,0.4980392,1,1","0","0"]}
{"type":"AmplifyShaderEditor.VertexToFragmentNode, AmplifyShaderEditor","id":50,"pos":[864,-496],"params":["Inherit","False","False","False","1","0","FLOAT3","0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.PowerNode, AmplifyShaderEditor","id":52,"pos":[-848,32],"params":["Inherit","False","False","2","0","FLOAT","0","False","1","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.SamplerNode, AmplifyShaderEditor","id":51,"pos":[1248,-528],"params":["Inherit","True","Property","_Tex","Cubemap (HDR)","2","1","[NoScaleOffset]","Create","False","0","0","0","False","0","False","","-1","None","8ea5fa8e069d8d946a1c0da7f077ba94","True","0","False","black","LockedToCube","False","Object","-1","Auto","Cube","False","8","0","SAMPLERCUBE","","False","1","FLOAT3","0,0,0","False","2","FLOAT","0","False","3","FLOAT3","0,0,0","False","4","FLOAT3","0,0,0","False","5","FLOAT","1","False","6","FLOAT","0","False","7","SAMPLERSTATE","","False","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.SaturateNode, AmplifyShaderEditor","id":54,"pos":[-656,32],"params":["Inherit","False","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":56,"pos":[-848,160],"params":["Half","False","Constant","_Float1","Float 1","55","0","Create","True","0","0","0","False","0","False","Object","-1","","0","0","0","0","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":53,"pos":[-864,256],"params":["Half","False","Property","_FogOpacity","Fog Opacity","7","0","Create","True","0","0","0","False","0","False","Object","-1","","0.5","0.13","0","1","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorSpaceDouble, AmplifyShaderEditor","id":55,"pos":[1632,-448],"params":["Inherit","False","0","5","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.RangedFloatNode, AmplifyShaderEditor","id":58,"pos":[1632,-96],"params":["Half","False","Property","_Exposure","Exposure","1","0","Create","True","0","0","0","False","0","False","Object","-1","","1","0.2","0","8","0","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.ColorNode, AmplifyShaderEditor","id":59,"pos":[1632,-272],"params":["Half","False","Property","_TintColor","Tint Color","0","1","[Gamma]","Create","True","0","0","0","False","1","Header(Cubemap)","False","Object","-1","","0.5,0.5,0.5,1","1,1,1,1","False","True","0","6","COLOR","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4","FLOAT3","5"]}
{"type":"AmplifyShaderEditor.CustomExpressionNode, AmplifyShaderEditor","id":57,"pos":[1632,-528],"params":["Half","False","DecodeHDR(Data, _Tex_HDR)","3","Create","1","True","Data","FLOAT4","0,0,0,0","In","","Float","False","DecodeHDR","True","False","0","","False","1","0","FLOAT4","0,0,0,0","False","1","FLOAT3","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":62,"pos":[-400,32],"params":["Inherit","False","3","0","FLOAT","0","False","1","FLOAT","0","False","2","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.CommentaryNode, AmplifyShaderEditor","id":64,"pos":[-2119.035,-1435.965],"params":["Inherit","False","618","357","","4","70","69","68","67","FINAL COLOR","1,1,1,1","0","0"]}
{"type":"AmplifyShaderEditor.SimpleMultiplyOpNode, AmplifyShaderEditor","id":63,"pos":[2144,-528],"params":["Inherit","False","4","4","0","FLOAT3","0,0,0","False","1","COLOR","0,0,0,0","False","2","COLOR","0,0,0,0","False","3","FLOAT","0","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":66,"pos":[-160,32],"params":["Half","False","FOG_MASK","-1","True","1","0","FLOAT","0","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.RegisterLocalVarNode, AmplifyShaderEditor","id":65,"pos":[2368,-528],"params":["Half","False","CUBEMAP","-1","True","1","0","COLOR","0,0,0,0","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":67,"pos":[-2069.035,-1193.965],"params":["Inherit","False","66","FOG_MASK","1","0","OBJECT","","False","1","FLOAT","0"]}
{"type":"AmplifyShaderEditor.FogAndAmbientColorsNode, AmplifyShaderEditor","id":68,"pos":[-2069.035,-1385.965],"params":["Inherit","False","unity_FogColor","0","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.GetLocalVarNode, AmplifyShaderEditor","id":69,"pos":[-2069.035,-1273.965],"params":["Inherit","False","65","CUBEMAP","1","0","OBJECT","","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.LerpOp, AmplifyShaderEditor","id":70,"pos":[-1685.035,-1385.965],"params":["Inherit","False","3","0","COLOR","0,0,0,0","False","1","COLOR","0,0,0,0","False","2","FLOAT","0","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.StaticSwitch, AmplifyShaderEditor","id":72,"pos":[-1397.035,-1289.965],"params":["Float","False","Property","_EnableFog","Enable Fog","6","0","Create","True","0","0","0","False","1","Header(Fog)","False","","0","0","1","True","","Toggle","2","Key0","Key1","Create","True","True","All","9","1","COLOR","0,0,0,0","False","0","COLOR","0,0,0,0","False","2","COLOR","0,0,0,0","False","3","COLOR","0,0,0,0","False","4","COLOR","0,0,0,0","False","5","COLOR","0,0,0,0","False","6","COLOR","0,0,0,0","False","7","COLOR","0,0,0,0","False","8","COLOR","0,0,0,0","False","1","COLOR","0"]}
{"type":"AmplifyShaderEditor.Vector4Node, AmplifyShaderEditor","id":73,"pos":[1248,-272],"params":["Half","False","Property","_Tex_HDR","DecodeInstructions","10","1","[HideInInspector]","Create","False","0","0","0","True","0","False","Object","-1","","0,0,0,0","1,1,0,0","0","5","FLOAT4","0","FLOAT","1","FLOAT","2","FLOAT","3","FLOAT","4"]}
{"type":"AmplifyShaderEditor.StandardSurfaceOutputNode, AmplifyShaderEditor","id":95,"pos":[-848,-1328],"params":["Float","False","True","-1","3","AmplifyShaderEditor.MaterialInspector","0","0","Unlit","Toon/CustomSkybox","False","False","False","False","False","False","False","False","False","True","False","False","False","False","False","False","False","False","False","False","False","Back","0","False","","0","False","","False","0","False","","0","False","","False","0","0","False","","0","Opaque","0.5","True","True","0","False","Opaque","","Geometry","All","14","all","True","True","True","True","0","False","","False","0","False","","255","False","","255","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","0","False","","False","2","15","10","25","False","0.5","True","0","0","False","","0","False","","0","0","False","","0","False","","0","False","","0","False","","0","False","0","0,0,0,0","VertexOffset","True","False","Cylindrical","False","True","Relative","0","","-1","-1","-1","-1","0","False","0","0","False","","-1","0","False","","0","0","0","False","0.1","False","","0","False","","False","16","0","FLOAT3","0,0,0","False","1","FLOAT3","0,0,0","False","2","FLOAT3","0,0,0","False","3","FLOAT","0","False","4","FLOAT","0","False","6","FLOAT3","0,0,0","False","7","FLOAT3","0,0,0","False","8","FLOAT","0","False","9","FLOAT","0","False","10","FLOAT","0","False","13","FLOAT3","0,0,0","False","11","FLOAT3","0,0,0","False","12","FLOAT3","0,0,0","False","16","FLOAT4","0,0,0,0","False","14","FLOAT4","0,0,0,0","False","15","FLOAT3","0,0,0","False","0"]}
{"wire":[5,0,3,0]}
{"wire":[5,1,2,0]}
{"wire":[7,0,4,0]}
{"wire":[7,1,5,0]}
{"wire":[11,0,8,2]}
{"wire":[11,1,8,1]}
{"wire":[9,0,7,0]}
{"wire":[14,0,10,0]}
{"wire":[14,1,11,0]}
{"wire":[14,2,8,4]}
{"wire":[12,0,9,0]}
{"wire":[17,0,14,0]}
{"wire":[16,0,12,0]}
{"wire":[18,0,16,0]}
{"wire":[18,1,15,0]}
{"wire":[21,0,12,0]}
{"wire":[22,0,12,0]}
{"wire":[25,0,12,0]}
{"wire":[27,0,25,0]}
{"wire":[27,1,20,0]}
{"wire":[27,2,18,0]}
{"wire":[28,0,26,2]}
{"wire":[28,1,23,0]}
{"wire":[29,0,20,0]}
{"wire":[29,1,19,0]}
{"wire":[29,2,20,0]}
{"wire":[30,0,22,0]}
{"wire":[30,1,20,0]}
{"wire":[30,2,21,0]}
{"wire":[35,0,31,0]}
{"wire":[32,0,27,0]}
{"wire":[32,1,29,0]}
{"wire":[32,2,30,0]}
{"wire":[33,0,26,0]}
{"wire":[34,0,26,1]}
{"wire":[34,1,28,0]}
{"wire":[34,2,26,3]}
{"wire":[36,0,35,0]}
{"wire":[37,0,32,0]}
{"wire":[37,1,33,0]}
{"wire":[39,0,34,0]}
{"wire":[44,0,36,1]}
{"wire":[46,1,39,0]}
{"wire":[46,0,37,0]}
{"wire":[47,0,41,0]}
{"wire":[49,0,44,0]}
{"wire":[49,1,45,0]}
{"wire":[49,2,40,0]}
{"wire":[49,3,45,0]}
{"wire":[49,4,42,0]}
{"wire":[50,0,46,0]}
{"wire":[52,0,49,0]}
{"wire":[52,1,47,0]}
{"wire":[51,1,50,0]}
{"wire":[54,0,52,0]}
{"wire":[57,0,51,0]}
{"wire":[62,0,54,0]}
{"wire":[62,1,56,0]}
{"wire":[62,2,53,0]}
{"wire":[63,0,57,0]}
{"wire":[63,1,55,0]}
{"wire":[63,2,59,0]}
{"wire":[63,3,58,0]}
{"wire":[66,0,62,0]}
{"wire":[65,0,63,0]}
{"wire":[70,0,68,0]}
{"wire":[70,1,69,0]}
{"wire":[70,2,67,0]}
{"wire":[72,1,69,0]}
{"wire":[72,0,70,0]}
{"wire":[95,2,72,0]}
ASEEND*/
//CHKSM=A35E8696EE7EF5EA6AA4EE9203B83C5DE420BD1B